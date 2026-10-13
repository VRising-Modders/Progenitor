using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>One <c>[BepInDependency]</c> a plugin image declares.</summary>
internal readonly struct PluginDependencyRef
{
    internal PluginDependencyRef(string guid, bool soft)
    {
        Guid = guid ?? "";
        Soft = soft;
    }

    internal string Guid { get; }

    /// <summary><c>DependencyFlags.SoftDependency</c>: order after it when present, never wait for it.</summary>
    internal bool Soft { get; }
}

/// <summary>
/// What a reload-folder DLL says about itself, read from metadata before anything of it loads: its
/// assembly name, the <c>[BepInPlugin]</c> GUIDs it defines, its <c>[BepInDependency]</c> list and the
/// assemblies it references. Plain data.
/// </summary>
internal sealed class PluginImageInfo
{
    internal static PluginImageInfo Empty(string file, string error) =>
        new(file, "", Array.Empty<string>(), Array.Empty<PluginDependencyRef>(), Array.Empty<string>(), error);

    internal PluginImageInfo(
        string file, string assemblyName, string[]? guids, PluginDependencyRef[]? dependencies, string[]? assemblyRefs, string? error = null)
    {
        File = file ?? "";
        AssemblyName = assemblyName ?? "";
        Guids = guids ?? Array.Empty<string>();
        Dependencies = dependencies ?? Array.Empty<PluginDependencyRef>();
        AssemblyRefs = assemblyRefs ?? Array.Empty<string>();
        Error = error ?? "";
    }

    /// <summary>The DLL's file name (no directory): the tie-break and the name every log line uses.</summary>
    internal string File { get; }
    internal string AssemblyName { get; }
    internal string[] Guids { get; }
    internal PluginDependencyRef[] Dependencies { get; }
    internal string[] AssemblyRefs { get; }
    /// <summary>Why the metadata could not be read, or "". An unreadable image is loaded in name order and fails there as before.</summary>
    internal string Error { get; }
}

/// <summary>One step of a load plan.</summary>
internal sealed class PluginLoadStep
{
    internal PluginLoadStep(int index, string waitingFor, string reason, bool inCycle, int[] hardProviders)
    {
        Index = index;
        WaitingFor = waitingFor ?? "";
        Reason = reason ?? "";
        InCycle = inCycle;
        HardProviders = hardProviders ?? Array.Empty<int>();
    }

    /// <summary>Index into the pending list the plan was made from.</summary>
    internal int Index { get; }

    /// <summary>Not loaded this pass: the GUID or file it waits for, else "".</summary>
    internal string WaitingFor { get; }
    internal bool Waiting => WaitingFor.Length > 0;

    /// <summary>The sentence a waiting step is logged and reported with.</summary>
    internal string Reason { get; }

    /// <summary>Part of a dependency cycle: loaded in file-name order with the rest of the cycle.</summary>
    internal bool InCycle { get; }

    /// <summary>
    /// Pending images this one cannot load without (a hard <c>[BepInDependency]</c> or an assembly reference
    /// they provide). The loader re-checks them right before the load: a provider that failed to load turns
    /// this step into a wait instead of a <c>FileNotFoundException</c>.
    /// </summary>
    internal int[] HardProviders { get; }
}

/// <summary>
/// Dependency-aware load order for <c>BepInEx/progenitor-reload/</c> (job FORD1). BepInEx's chainloader
/// sorts plugins by <c>[BepInDependency]</c>; the reloader used to load the folder in file-name order and
/// give each plugin a load context that could resolve only Progenitor, so a mod that needs another
/// reload-folder mod (AutoLatchGates needs ExampleCommandFramework) failed with
/// <c>FileNotFoundException</c> and was never retried.
///
/// <para>The plan, for every DLL that is about to load:</para>
/// <list type="bullet">
/// <item>it loads after every pending DLL it depends on by GUID (hard or soft) or whose assembly it
///   references;</item>
/// <item>a hard dependency that is neither pending, already loaded nor a chainloader plugin makes it
///   <b>wait</b> (reported, retried every scan, never stamped as failed), and anything that needs a waiting
///   DLL waits too; a soft dependency never makes anything wait;</item>
/// <item>ties keep file-name order, so a folder without dependencies loads exactly as before;</item>
/// <item>a cycle loads in file-name order with one line naming it.</item>
/// </list>
///
/// <para>Game-free: reads metadata only. <c>PluginLoadOrderTests</c> pins it.</para>
/// </summary>
internal static class PluginLoadOrder
{
    internal const string BepInPluginAttribute = "BepInEx.BepInPlugin";
    internal const string BepInDependencyAttribute = "BepInEx.BepInDependency";

    /// <summary><c>BepInDependency.DependencyFlags.SoftDependency</c>.</summary>
    internal const int SoftDependencyFlag = 2;

    // ---- reading an image --------------------------------------------------------------------

    /// <summary>Read <paramref name="image"/>'s plugin metadata. Never throws.</summary>
    internal static PluginImageInfo Read(string? file, byte[]? image)
    {
        string name = file ?? "";
        if (image == null || image.Length == 0) return PluginImageInfo.Empty(name, "empty image");
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return PluginImageInfo.Empty(name, "no metadata");
            var md = pe.GetMetadataReader();
            string asm = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "";

            var guids = new List<string>();
            var deps = new List<PluginDependencyRef>();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                foreach (var ah in td.GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(ah);
                    string attr = AttributeTypeName(md, ca);
                    if (attr == BepInPluginAttribute)
                    {
                        if (TryDecodeArgs(md, ca, out var guid, out _, out _) && guid.Length > 0 && !Contains(guids, guid))
                            guids.Add(guid);
                    }
                    else if (attr == BepInDependencyAttribute)
                    {
                        if (!TryDecodeArgs(md, ca, out var guid, out bool secondIsInt, out int flags) || guid.Length == 0) continue;
                        bool soft = secondIsInt && (flags & SoftDependencyFlag) != 0;
                        int existing = deps.FindIndex(d => string.Equals(d.Guid, guid, StringComparison.Ordinal));
                        if (existing < 0) deps.Add(new PluginDependencyRef(guid, soft));
                        else if (deps[existing].Soft && !soft) deps[existing] = new PluginDependencyRef(guid, false);
                    }
                }
            }

            var refs = new List<string>();
            foreach (var arh in md.AssemblyReferences)
            {
                string r = md.GetString(md.GetAssemblyReference(arh).Name);
                if (r.Length > 0 && !Contains(refs, r)) refs.Add(r);
            }
            return new PluginImageInfo(name, asm, guids.ToArray(), deps.ToArray(), refs.ToArray());
        }
        catch (Exception e)
        {
            return PluginImageInfo.Empty(name, e.GetType().Name + ": " + e.Message);
        }
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute ca)
    {
        EntityHandle type;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();
        else
            return "";
        if (type.Kind == HandleKind.TypeReference)
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)type);
            return Join(md.GetString(tr.Namespace), md.GetString(tr.Name));
        }
        if (type.Kind == HandleKind.TypeDefinition)
        {
            var td = md.GetTypeDefinition((TypeDefinitionHandle)type);
            return Join(md.GetString(td.Namespace), md.GetString(td.Name));
        }
        return "";
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    /// <summary>
    /// The first fixed argument (a string) and, when the constructor's second parameter is not a string,
    /// that argument as an int (an enum's underlying value: <c>DependencyFlags</c>).
    /// </summary>
    private static bool TryDecodeArgs(MetadataReader md, CustomAttribute ca, out string first, out bool secondIsInt, out int second)
    {
        first = "";
        secondIsInt = false;
        second = 0;
        var reader = md.GetBlobReader(ca.Value);
        if (reader.Length < 2 || reader.ReadUInt16() != 1) return false;
        int paramCount = ParameterKinds(md, ca, out bool firstIsString, out bool secondIsString);
        if (paramCount < 1 || !firstIsString) return false;
        first = reader.ReadSerializedString() ?? "";
        if (paramCount >= 2 && !secondIsString && reader.RemainingBytes >= 4)
        {
            second = reader.ReadInt32();
            secondIsInt = true;
        }
        return true;
    }

    private static int ParameterKinds(MetadataReader md, CustomAttribute ca, out bool firstIsString, out bool secondIsString)
    {
        firstIsString = secondIsString = false;
        BlobHandle sig;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            sig = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Signature;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            sig = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).Signature;
        else
            return 0;
        var blob = md.GetBlobReader(sig);
        var decoded = new SignatureDecoder<string, object?>(KindProvider.Instance, md, null).DecodeMethodSignature(ref blob);
        var ps = decoded.ParameterTypes;
        if (ps.Length > 0) firstIsString = ps[0] == "string";
        if (ps.Length > 1) secondIsString = ps[1] == "string";
        return ps.Length;
    }

    /// <summary>Just enough of a signature provider to tell a string parameter from anything else.</summary>
    private sealed class KindProvider : ISignatureTypeProvider<string, object?>
    {
        internal static readonly KindProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode == PrimitiveTypeCode.String ? "string" : typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "type";
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "type";
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }

    // ---- planning ----------------------------------------------------------------------------

    /// <summary>
    /// Order <paramref name="pending"/> for loading. <paramref name="loadedGuids"/> and
    /// <paramref name="loadedAssemblies"/> are what is already running (reload-folder plugins, plus the
    /// chainloader's GUIDs). <paramref name="externalKnown"/> false means the chainloader's list could not be
    /// read: then a hard dependency outside the folder never makes a plugin wait (the old behavior), because
    /// a wrong wait would keep a working mod from loading at all.
    /// </summary>
    internal static List<PluginLoadStep> Plan(
        IReadOnlyList<PluginImageInfo>? pending,
        IEnumerable<string>? loadedGuids,
        IEnumerable<string>? loadedAssemblies,
        bool externalKnown)
    {
        var list = pending ?? Array.Empty<PluginImageInfo>();
        int n = list.Count;
        var haveGuid = new HashSet<string>(loadedGuids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var haveAsm = new HashSet<string>(loadedAssemblies ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Providers among the pending images; the first in file-name order wins a duplicate.
        var byName = Indices(n);
        byName.Sort((a, b) => CompareFiles(list[a], list[b], a, b));
        var guidProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var asmProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (int i in byName)
        {
            foreach (var g in list[i].Guids)
                if (!guidProvider.ContainsKey(g)) guidProvider[g] = i;
            if (list[i].AssemblyName.Length > 0 && !asmProvider.ContainsKey(list[i].AssemblyName))
                asmProvider[list[i].AssemblyName] = i;
        }

        var hardEdges = new List<int>[n];   // i cannot load without j
        var softEdges = new List<int>[n];   // i loads after j when both load
        var waitingFor = new string[n];
        var reason = new string[n];
        for (int i = 0; i < n; i++)
        {
            hardEdges[i] = new List<int>();
            softEdges[i] = new List<int>();
            waitingFor[i] = "";
            reason[i] = "";
            var info = list[i];
            foreach (var dep in info.Dependencies)
            {
                if (dep.Guid.Length == 0) continue;
                if (Contains(info.Guids, dep.Guid)) continue;
                if (guidProvider.TryGetValue(dep.Guid, out int j) && j != i)
                {
                    AddOnce(dep.Soft ? softEdges[i] : hardEdges[i], j);
                    continue;
                }
                if (dep.Soft || haveGuid.Contains(dep.Guid) || !externalKnown) continue;
                if (waitingFor[i].Length == 0)
                {
                    waitingFor[i] = dep.Guid;
                    reason[i] = FormatWaitingMissing(info.File, dep.Guid);
                }
            }
            foreach (var r in info.AssemblyRefs)
            {
                if (haveAsm.Contains(r)) continue;
                if (asmProvider.TryGetValue(r, out int j) && j != i) AddOnce(hardEdges[i], j);
            }
        }

        // A plugin that cannot load without a waiting one waits too, naming the first link.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (int i in byName)
            {
                if (waitingFor[i].Length > 0) continue;
                foreach (int j in hardEdges[i])
                {
                    if (waitingFor[j].Length == 0) continue;
                    waitingFor[i] = list[j].File;
                    reason[i] = FormatWaitingOnWaiting(list[i].File, list[j].File, waitingFor[j]);
                    grew = true;
                    break;
                }
            }
        }

        // Kahn over the plugins that will load; ties in file-name order.
        var indegree = new int[n];
        var dependents = new List<int>[n];
        for (int i = 0; i < n; i++) dependents[i] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (waitingFor[i].Length > 0) continue;
            foreach (int j in Union(hardEdges[i], softEdges[i]))
            {
                if (waitingFor[j].Length > 0) continue; // a soft edge to a waiting plugin is no edge
                indegree[i]++;
                dependents[j].Add(i);
            }
        }

        var steps = new List<PluginLoadStep>(n);
        var placed = new bool[n];
        var ready = new List<int>();
        foreach (int i in byName)
            if (waitingFor[i].Length == 0 && indegree[i] == 0) ready.Add(i);
        while (true)
        {
            bool forced = false;
            if (ready.Count == 0)
            {
                // Stuck: what is left is a cycle or depends on one. Break it at the first plugin in
                // file-name order that sits on a cycle, and carry on, so a plugin that merely depends
                // on the cycle still loads after it.
                int pick = -1;
                foreach (int i in byName)
                    if (!placed[i] && waitingFor[i].Length == 0 && OnCycle(i, hardEdges, softEdges, waitingFor, placed))
                    {
                        pick = i;
                        break;
                    }
                if (pick < 0)
                    foreach (int i in byName)
                        if (!placed[i] && waitingFor[i].Length == 0) { pick = i; break; }
                if (pick < 0) break;
                ready.Add(pick);
                forced = true;
            }
            int next = ready[0];
            ready.RemoveAt(0);
            if (placed[next]) continue;
            placed[next] = true;
            steps.Add(new PluginLoadStep(next, "", "", forced, hardEdges[next].ToArray()));
            var released = new List<int>();
            foreach (int d in dependents[next])
                if (--indegree[d] == 0 && !placed[d]) released.Add(d);
            if (released.Count == 0) continue;
            ready.AddRange(released);
            ready.Sort((a, b) => CompareFiles(list[a], list[b], a, b));
        }

        foreach (int i in byName)
            if (waitingFor[i].Length > 0)
                steps.Add(new PluginLoadStep(i, waitingFor[i], reason[i], false, hardEdges[i].ToArray()));
        return steps;
    }

    /// <summary>
    /// True when loaded plugin <paramref name="dependent"/> cannot keep running without
    /// <paramref name="provider"/>: a hard <c>[BepInDependency]</c> on one of its GUIDs, or a reference to its
    /// assembly (the dependent's context resolved that assembly from the provider's).
    /// </summary>
    internal static bool NeedsAtRuntime(PluginImageInfo? dependent, PluginImageInfo? provider)
    {
        if (dependent == null || provider == null || ReferenceEquals(dependent, provider)) return false;
        foreach (var dep in dependent.Dependencies)
            if (!dep.Soft && Contains(provider.Guids, dep.Guid)) return true;
        return provider.AssemblyName.Length > 0 && Contains(dependent.AssemblyRefs, provider.AssemblyName);
    }

    /// <summary>
    /// <paramref name="seeds"/> plus every image in <paramref name="all"/> that needs one of them, transitively
    /// (<see cref="NeedsAtRuntime"/>). Indices into <paramref name="all"/>, seeds first.
    /// </summary>
    internal static List<int> DependentClosure(IReadOnlyList<PluginImageInfo> all, IEnumerable<int> seeds)
    {
        var result = new List<int>();
        var inSet = new bool[all.Count];
        foreach (int s in seeds)
        {
            if (s < 0 || s >= all.Count || inSet[s]) continue;
            inSet[s] = true;
            result.Add(s);
        }
        for (int k = 0; k < result.Count; k++)
        {
            var provider = all[result[k]];
            for (int i = 0; i < all.Count; i++)
            {
                if (inSet[i] || !NeedsAtRuntime(all[i], provider)) continue;
                inSet[i] = true;
                result.Add(i);
            }
        }
        return result;
    }

    // ---- the words ---------------------------------------------------------------------------

    internal static string FormatWaitingMissing(string file, string guid) =>
        "Reload: " + file + " is waiting for " + guid + ", which it needs ([BepInDependency]) and which is not installed; "
        + "it loads by itself once that plugin is in the reload folder or BepInEx/plugins.";

    internal static string FormatWaitingOnWaiting(string file, string provider, string providerWaitsFor) =>
        "Reload: " + file + " is waiting for " + provider + ", which it needs and which is itself waiting for "
        + providerWaitsFor + ".";

    internal static string FormatProviderDidNotLoad(string file, string provider) =>
        "Reload: " + file + " is waiting for " + provider + ", which it needs and which did not load; "
        + "it loads by itself once " + provider + " does.";

    internal static string FormatCycle(IEnumerable<string> files) =>
        "Reload: dependency cycle between " + string.Join(", ", files) + "; loading them in file-name order.";

    internal static string FormatOrder(IEnumerable<string> files) =>
        "Reload: load order " + string.Join(" -> ", files) + " (dependencies first).";

    internal static string FormatDependentFirst(string dependent, string provider) =>
        "Reload: " + dependent + " unloads first and reloads after: it needs " + provider + ", which is being replaced or removed.";

    internal static string FormatKeptForDependent(string file) =>
        "Reload: Core swap keeps " + file + " loaded: a plugin that registers its own IL2CPP types needs it.";

    internal static string FormatHeldByPinnedDependent(string provider, string dependent) =>
        "Reload: " + provider + " changed, but " + dependent + " needs it and cannot be reloaded in place; "
        + "the running build stays loaded. Restart required.";

    // ---- the row Core reads ------------------------------------------------------------------

    /// <summary><c>file \t guid|guid \t waitingFor \t reason</c>; Core's <c>WaitingPluginRow</c> mirrors it.</summary>
    internal static string FormatWaitingRow(string file, IEnumerable<string>? guids, string waitingFor, string reason)
    {
        var sb = new StringBuilder();
        sb.Append(Clean(file)).Append('\t');
        var cleaned = new List<string>();
        foreach (var g in guids ?? Array.Empty<string>()) if (!string.IsNullOrEmpty(g)) cleaned.Add(Clean(g));
        sb.Append(string.Join("|", cleaned)).Append('\t');
        sb.Append(Clean(waitingFor)).Append('\t');
        sb.Append(Clean(reason));
        return sb.ToString();
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace('\t', ' ').Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>True when <paramref name="start"/> can reach itself through unplaced, non-waiting plugins.</summary>
    private static bool OnCycle(int start, List<int>[] hard, List<int>[] soft, string[] waiting, bool[] placed)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        foreach (int j in Union(hard[start], soft[start])) stack.Push(j);
        while (stack.Count > 0)
        {
            int k = stack.Pop();
            if (k == start) return true;
            if (placed[k] || waiting[k].Length > 0 || !seen.Add(k)) continue;
            foreach (int j in Union(hard[k], soft[k])) stack.Push(j);
        }
        return false;
    }

    private static int CompareFiles(PluginImageInfo a, PluginImageInfo b, int ia, int ib)
    {
        int c = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : ia.CompareTo(ib);
    }

    private static List<int> Indices(int n)
    {
        var l = new List<int>(n);
        for (int i = 0; i < n; i++) l.Add(i);
        return l;
    }

    private static IEnumerable<int> Union(List<int> a, List<int> b)
    {
        foreach (int x in a) yield return x;
        foreach (int x in b) if (!a.Contains(x)) yield return x;
    }

    private static void AddOnce(List<int> l, int x)
    {
        if (!l.Contains(x)) l.Add(x);
    }

    private static bool Contains(IEnumerable<string> items, string value)
    {
        foreach (var s in items)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
