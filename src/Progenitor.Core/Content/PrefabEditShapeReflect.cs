using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Progenitor.Content;

/// <summary>
/// Fills <see cref="PrefabEditShape"/>'s field facts from reflection over the receiver's own
/// loaded type (job FEDW3, R51 finding 1). Reflection and <c>Marshal</c> only — no game type —
/// so the suite compiles this file and drives the rule against real structs it defines itself,
/// including ones carrying <c>IntPtr</c>, a nested pointer and an <c>Entity</c>-shaped field.
///
/// <para>Offsets and sizes come from <c>Marshal.OffsetOf</c> / <c>Marshal.SizeOf</c> — the same
/// measure the wire's length check and <c>Marshal.PtrToStructure</c> use. Nothing here reads
/// <c>TypeManager</c> and nothing multiplies a runtime-read number (CLAUDE.md, EV17).</para>
///
/// <para>The answer is cached per type, negatives included, and the cache is cleared with the Core
/// generation: a resolved <c>Type</c> must not outlive the generation that resolved it.</para>
/// </summary>
internal static class PrefabEditShapeReflect
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Entry> Cache = new();

    private readonly struct Entry
    {
        public Entry(PrefabEditTypeShape? shape, string reason)
        {
            Shape = shape;
            Reason = reason;
        }

        public PrefabEditTypeShape? Shape { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Lines this generation produced that are not a refusal and must still be said once: today
    /// only the value domain's dump-literal cross-check (job FEDW6, R54 finding 3). This file is
    /// game-free, so it never logs; the caller that owns a logger drains and writes them.
    /// </summary>
    private static readonly List<string> Notices = new();
    private static readonly HashSet<string> NoticedKeys = new(StringComparer.Ordinal);

    /// <summary>Core install / uninstall: a cached <c>Type</c> must not cross a generation.</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            Notices.Clear();
            NoticedKeys.Clear();
        }
    }

    /// <summary>
    /// Take the notices produced since the last drain. Each distinct line is produced at most once
    /// per Core generation, so a caller that drains on every resolve still logs it once.
    /// </summary>
    public static string[] DrainNotices()
    {
        lock (Gate)
        {
            if (Notices.Count == 0) return Array.Empty<string>();
            var taken = Notices.ToArray();
            Notices.Clear();
            return taken;
        }
    }

    private static void Notice(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Gate)
        {
            if (!NoticedKeys.Add(line)) return;
            Notices.Add(line);
        }
    }

    /// <summary>Types classified in this generation (diagnostics and tests).</summary>
    public static int CachedTypeCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    /// <summary>
    /// Classify <paramref name="type"/>, cached. False refuses the component: the type is not a
    /// formatted struct, its layout could not be read, or every byte of it is reference-shaped.
    /// </summary>
    public static bool TryDescribe(Type? type, out PrefabEditTypeShape shape, out string reason)
    {
        shape = null!;
        if (type == null)
        {
            reason = "no type";
            return false;
        }
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out var hit))
            {
                shape = hit.Shape!;
                reason = hit.Reason;
                return hit.Shape != null;
            }
        }

        PrefabEditTypeShape? built = null;
        string why;
        try
        {
            int size = Marshal.SizeOf(type);
            var fields = DescribeFields(type, 0);
            if (PrefabEditShape.TryClassify(type.FullName ?? type.Name, size, fields, type, out var s, out why))
            {
                built = s;
                // The value domain rides with the shape because it is resolved from the same
                // reflection over the same type, once per type per Core generation (job FEDW5,
                // R53 finding 2). A type that is not on the wire-safe list has no domain: it is
                // refused at the choke point before anything asks.
                if (PrefabEditWireTypes.TryGet(type.FullName, out var entry))
                    built.SetValueDomain(BuildValueDomain(type, entry));
            }
        }
        catch (Exception e)
        {
            why = "layout could not be read: " + e.Message;
        }

        lock (Gate) Cache[type] = new Entry(built, why);
        shape = built!;
        reason = why;
        return built != null;
    }

    /// <summary>
    /// Field facts for one struct, or <b>null</b> when reflection could not enumerate them at all
    /// (R52 finding 1: "no fields" and "could not ask" used to be the same empty list, and the
    /// classifier read both as "nothing to preserve"). A field whose layout cannot be measured is
    /// reported with <see cref="PrefabEditFieldForm.Unknown"/>, which the classifier preserves
    /// rather than writes; that is the safe direction for something reflection could not explain.
    /// </summary>
    public static IReadOnlyList<PrefabEditFieldFact>? DescribeFields(Type type, int depth)
    {
        var facts = new List<PrefabEditFieldFact>();
        FieldInfo[] fields;
        try
        {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return null;
        }
        if (fields == null) return null;

        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var ft = f.FieldType;
            int offset;
            try { offset = (int)Marshal.OffsetOf(type, f.Name); }
            catch { offset = -1; }

            var form = FormOf(ft);
            int size = SizeOf(f, ft, form);
            IReadOnlyList<PrefabEditFieldFact>? nested = null;
            if (form == PrefabEditFieldForm.Struct
                && size > 0
                && depth + 1 < PrefabEditShape.MaxDepth
                && !PrefabEditShape.IsReferenceShapedName(ft.FullName ?? ft.Name))
            {
                try { nested = DescribeFields(ft, depth + 1); }
                catch { nested = null; }
            }

            facts.Add(new PrefabEditFieldFact(
                f.Name,
                ft.FullName ?? ft.Name,
                offset,
                size,
                form,
                nested));
        }
        return facts;
    }

    private static PrefabEditFieldForm FormOf(Type ft)
    {
        if (ft.IsPointer || ft.IsByRef) return PrefabEditFieldForm.Pointer;
        if (ft == typeof(IntPtr) || ft == typeof(UIntPtr)) return PrefabEditFieldForm.Pointer;
        if (ft.IsEnum) return PrefabEditFieldForm.Enum;
        if (ft == typeof(bool)) return PrefabEditFieldForm.Bool;
        if (ft == typeof(byte) || ft == typeof(sbyte)
            || ft == typeof(short) || ft == typeof(ushort)
            || ft == typeof(int) || ft == typeof(uint)
            || ft == typeof(long) || ft == typeof(ulong)
            || ft == typeof(float) || ft == typeof(double)
            || ft == typeof(char))
            return PrefabEditFieldForm.Numeric;
        if (ft.IsValueType) return PrefabEditFieldForm.Struct;
        return PrefabEditFieldForm.Unknown;
    }

    private static int SizeOf(FieldInfo field, Type ft, PrefabEditFieldForm form)
    {
        if (form == PrefabEditFieldForm.Pointer) return IntPtr.Size;
        // A bool is four marshaled bytes only when nothing says otherwise. Every interop struct
        // this wire carries declares [MarshalAs(UnmanagedType.U1)] on its bools next to an explicit
        // native FieldOffset, so the field is one byte and the next field starts one byte later:
        // ProjectM.ItemData's RemoveOnConsume at 40 is followed by SortOrder at 42, and
        // ModifyBloodDrainBuff packs three bools at 32, 33, 34 of a 36-byte component. Measuring
        // those as four bytes each made the last one run past the end of its own type and the fit
        // check in PrefabEditShape.Walk refused the whole component (job FEDW4).
        if (form == PrefabEditFieldForm.Bool) return MarshalledBoolSize(field);
        // Marshal.SizeOf refuses an enum type outright ("cannot be marshaled as an unmanaged
        // structure"), so an enum is measured by its underlying type, which is what the marshaler
        // actually lays down. Measured, not assumed: without this every enum field read -1 and the
        // whole component was refused as not fitting its own size.
        if (form == PrefabEditFieldForm.Enum)
        {
            try { return Marshal.SizeOf(Enum.GetUnderlyingType(ft)); }
            catch { return -1; }
        }
        try { return Marshal.SizeOf(ft); }
        catch { return -1; }
    }

    // ---- the value domain (job FEDW5, R53 finding 2) ------------------------------------------

    /// <summary>
    /// Resolve one type's per-field value policies against the receiver's own copy of that type:
    /// where each policied field lives in the marshaled bytes, whether its storage is signed, and
    /// — for an enum rule — the members <em>this build</em> declares.
    ///
    /// <para>Fails closed. A policy naming a field this build does not have, an enum rule over a
    /// field that is not an enum, or a width no rule can read produces a domain that refuses every
    /// record of the type with the stale-list line shape, rather than one that quietly checks
    /// nothing.</para>
    /// </summary>
    public static PrefabEditValueDomain BuildValueDomain(Type type, PrefabEditWireType entry)
    {
        if (type == null || entry == null || entry.ValuePolicies.Length == 0)
            return PrefabEditValueDomain.None;

        var checks = new List<PrefabEditValueCheck>(entry.ValuePolicies.Length);
        for (int i = 0; i < entry.ValuePolicies.Length; i++)
        {
            var policy = entry.ValuePolicies[i];
            var field = type.GetField(policy.Field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                return Stale(entry, "the value policy names a field '" + policy.Field + "' this build's type does not have");

            var ft = field.FieldType;
            int offset;
            try { offset = (int)Marshal.OffsetOf(type, field.Name); }
            catch (Exception e) { return Stale(entry, "the offset of '" + policy.Field + "' could not be read: " + e.Message); }

            bool isEnumRule = policy.Rule == PrefabEditValueRule.EnumMember
                || policy.Rule == PrefabEditValueRule.EnumBits;
            if (isEnumRule && !ft.IsEnum)
                return Stale(entry, "'" + policy.Field + "' carries an enum rule but is " + (ft.FullName ?? ft.Name) + " on this build");
            if (policy.Rule == PrefabEditValueRule.Bool01 && ft != typeof(bool))
                return Stale(entry, "'" + policy.Field + "' carries a bool rule but is " + (ft.FullName ?? ft.Name) + " on this build");
            if (policy.Rule == PrefabEditValueRule.Finite && ft != typeof(float) && ft != typeof(double))
                return Stale(entry, "'" + policy.Field + "' carries a float rule but is " + (ft.FullName ?? ft.Name) + " on this build");

            int size;
            bool unsigned;
            if (ft.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(ft);
                try { size = Marshal.SizeOf(underlying); }
                catch (Exception e) { return Stale(entry, "the width of '" + policy.Field + "' could not be read: " + e.Message); }
                unsigned = IsUnsigned(underlying);
            }
            else if (ft == typeof(bool))
            {
                size = MarshalledBoolSize(field);
                unsigned = true;
            }
            else
            {
                try { size = Marshal.SizeOf(ft); }
                catch (Exception e) { return Stale(entry, "the width of '" + policy.Field + "' could not be read: " + e.Message); }
                unsigned = IsUnsigned(ft);
            }
            if (size <= 0 || offset < 0)
                return Stale(entry, "'" + policy.Field + "' measures " + size + " byte(s) at offset " + offset);

            long[] members = Array.Empty<long>();
            long mask = 0;
            switch (policy.Rule)
            {
                case PrefabEditValueRule.EnumMember:
                    members = DeclaredMembers(ft, unsigned);
                    if (members.Length == 0)
                        return Stale(entry, "'" + policy.Field + "' is an enum with no declared member on this build");
                    break;

                case PrefabEditValueRule.EnumBits:
                    mask = SingleBitUnion(DeclaredMembers(ft, unsigned));
                    if (mask == 0)
                        return Stale(entry, "'" + policy.Field + "' is a flags enum with no single-bit member on this build");
                    break;

                case PrefabEditValueRule.NamedEnumBits:
                    {
                        // The field is a bare integer, so the enum has to be found by name — and
                        // this is the one rule that used to fail *open* (job FEDW6, R54 finding 3):
                        // an enum it could not resolve dropped silently onto the dump-cited
                        // literal, so the design's central claim ("the members are read from the
                        // receiver's own copy of the enum, so the domain follows the build") could
                        // stop being true for this field with nobody told. It now fails closed like
                        // every other resolution failure in this method.
                        //
                        // The fallback search is the same set the wire's type resolver walks — the
                        // type's own assembly, then every loaded ProjectM/Stunlock/Unity assembly —
                        // because this interop's assembly split is not the stable thing a
                        // single-assembly lookup assumes: R54 measured ItemData and LifeTime in
                        // ProjectM.Shared.dll, DisableAggroBuff and ModifyBloodDrainBuff in
                        // ProjectM.dll, and ProjectM.ModificationType (used by a struct in
                        // ProjectM.dll) in ProjectM.Shared.dll.
                        // Where the seed can name the declaring assembly it does, and that name is
                        // what decides the type (job FEDW7, R55 finding 6): the search below is a
                        // fallback for a build that moved the enum, and a fallback that has to
                        // choose between two answers refuses instead of choosing.
                        var named = ResolveEnum(
                            type, policy.EnumTypeName, policy.EnumAssemblyName,
                            out string searched, out string ambiguity);
                        if (ambiguity.Length > 0)
                            return Stale(entry, ambiguity);
                        if (named == null)
                            return Stale(entry, "the bit enum '" + policy.EnumTypeName + "' that '"
                                + policy.Field + "' carries does not resolve on this build (searched "
                                + searched + ")");
                        mask = SingleBitUnion(DeclaredMembers(named, IsUnsigned(Enum.GetUnderlyingType(named))));
                        if (mask == 0)
                            return Stale(entry, "'" + policy.Field + "' has no declared bits on this build");
                        // The dump-cited literal is a cross-check and never a fallback: what is
                        // enforced is always the mask this build declares. A difference means the
                        // seed's citation is stale, which is a thing to fix in the list, so it is
                        // said out loud once per Core generation rather than absorbed.
                        if (policy.Max != 0 && mask != policy.Max)
                            Notice(PrefabEditLog.ValueDomainMaskDrift(
                                entry.TypeFullName, policy.Field, policy.EnumTypeName, mask, policy.Max));
                        break;
                    }
            }

            checks.Add(new PrefabEditValueCheck(policy, offset, size, unsigned, members, mask));
        }
        return new PrefabEditValueDomain(checks.ToArray());
    }

    private static PrefabEditValueDomain Stale(PrefabEditWireType entry, string detail) =>
        new(Array.Empty<PrefabEditValueCheck>(),
            "the wire-safe list is stale for " + entry.TypeFullName + ": " + detail
            + " (PrefabEditWireTypes value policy; " + entry.Note + ")");

    /// <summary>
    /// Resolve an enum named by the wire-safe list, in an order where nothing is decided by which
    /// assembly happened to load first (job FEDW7, R55 finding 6):
    ///
    /// <list type="number">
    /// <item><b>The assembly the seed names.</b> Every <c>NamedEnumBits</c> entry records the
    /// assembly the interop declares its enum in, so the ordinary case is a lookup in exactly one
    /// named assembly and no search happens at all.</item>
    /// <item><b>The naming type's own assembly</b>, which is one specific assembly rather than a
    /// search — this is the path the suite's fixtures take, and the path a build that moved the
    /// enum next to its component takes.</item>
    /// <item><b>Every already-loaded assembly whose simple name passes
    /// <see cref="PrefabEditWire.IsAllowedAssemblyName"/></b> — and here every match is collected
    /// rather than the first one taken. Two assemblies declaring the same enum full name
    /// <em>refuse</em> the component through <paramref name="ambiguity"/>, because that allowlist
    /// is a <c>StartsWith</c> prefix test (<c>ProjectMagic.dll</c> passes it) and this rule has no
    /// downstream backstop: unlike a component type, whose resolution is re-checked against
    /// <c>Marshal.SizeOf</c> by <c>PrefabEditWireTypes.Agrees</c>, a wrong enum here silently
    /// becomes the mask enforced against bytes another machine wrote.</item>
    /// <item><b>The runtime's own lookup</b>, for a name that is already assembly-qualified.</item>
    /// </list>
    ///
    /// <para>Nothing is ever loaded to satisfy this, on any step.</para>
    ///
    /// <para><paramref name="searched"/> names where it looked, so a refusal says what the next
    /// reader has to check rather than only that something did not resolve.</para>
    /// </summary>
    private static Type? ResolveEnum(
        Type near, string fullName, string assemblyName, out string searched, out string ambiguity)
    {
        searched = "nowhere: the policy names no enum";
        ambiguity = "";
        if (string.IsNullOrEmpty(fullName)) return null;

        var looked = new List<string>(8);
        var nearHit = FromAssembly(near.Assembly, fullName, looked);
        var candidates = SearchAllowedAssemblies(near.Assembly, fullName, looked);
        searched = Describe(looked);

        // 1. The assembly the seed names. One named assembly is not a search, so there is no order
        //    to depend on; two loaded assemblies sharing that simple name is, and refuses.
        if (!string.IsNullOrEmpty(assemblyName))
        {
            if (string.Equals(SimpleName(near.Assembly), assemblyName, StringComparison.Ordinal) && nearHit != null)
                return nearHit;
            var stated = DistinctTypes(candidates, assemblyName);
            if (stated.Count == 1) return stated[0];
            if (stated.Count > 1)
            {
                ambiguity = Ambiguous(fullName, candidates, assemblyName);
                return null;
            }
            // The named assembly did not answer — a build that moved the enum, or a side that has
            // not loaded that assembly. Fall through to the same steps an unnamed enum takes.
        }

        // 2. The naming type's own assembly.
        if (nearHit != null) return nearHit;

        // 3. The searched set, all of it.
        var all = DistinctTypes(candidates, null);
        if (all.Count == 1) return all[0];
        if (all.Count > 1)
        {
            ambiguity = Ambiguous(fullName, candidates, null);
            return null;
        }

        // 4. An already-qualified name.
        looked.Add("Type.GetType");
        searched = Describe(looked);
        try
        {
            var t = Type.GetType(fullName, throwOnError: false);
            if (t != null && t.IsEnum) return t;
        }
        catch
        {
            // A name that cannot even be parsed is a name that does not resolve.
        }
        return null;
    }

    /// <summary>One enum type an assembly declares under the searched name.</summary>
    internal readonly struct PrefabEditEnumCandidate
    {
        public PrefabEditEnumCandidate(string assemblyName, Type type)
        {
            AssemblyName = assemblyName ?? "";
            Type = type;
        }

        public string AssemblyName { get; }
        public Type Type { get; }
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Test seam for step 3 of <see cref="ResolveEnum"/>: given the enum's full name, the matches
    /// the loaded allowed assemblies declare. Production leaves it null and walks
    /// <c>AppDomain.CurrentDomain</c>. It exists because the ambiguous case — two loaded assemblies
    /// declaring one enum full name — cannot be built from a single test assembly, and the case
    /// that must fail closed is exactly the one no fixture can reach.
    ///
    /// <para>Compiled only into the test build (job FEX2, R56 finding 11). What it overrides
    /// decides the mask enforced against a value another machine wrote into this process's memory,
    /// so the seam does not ship, the same way <c>Units.MonotonicSeconds</c>'s <c>TestClock</c>
    /// does not.</para>
    /// </summary>
    internal static Func<string, IReadOnlyList<PrefabEditEnumCandidate>>? EnumSearchOverride;
#endif

    private static IReadOnlyList<PrefabEditEnumCandidate> SearchAllowedAssemblies(
        Assembly nearAssembly, string fullName, List<string> looked)
    {
#if PROGENITOR_TESTS
        var seam = EnumSearchOverride;
        if (seam != null)
        {
            IReadOnlyList<PrefabEditEnumCandidate>? injected = null;
            try { injected = seam(fullName); }
            catch { injected = null; }
            var fromSeam = new List<PrefabEditEnumCandidate>();
            if (injected != null)
                for (int i = 0; i < injected.Count; i++)
                {
                    if (injected[i].Type == null || !injected[i].Type.IsEnum) continue;
                    looked.Add(injected[i].AssemblyName);
                    fromSeam.Add(injected[i]);
                }
            return fromSeam;
        }
#endif

        var found = new List<PrefabEditEnumCandidate>();
        Assembly[] loaded;
        try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return found; }
        for (int i = 0; i < loaded.Length; i++)
        {
            if (ReferenceEquals(loaded[i], nearAssembly)) continue;
            string? simple;
            try { simple = loaded[i].GetName().Name; }
            catch { continue; }
            if (!PrefabEditWire.IsAllowedAssemblyName(simple)) continue;
            var t = FromAssembly(loaded[i], fullName, looked);
            if (t != null) found.Add(new PrefabEditEnumCandidate(simple ?? "(unnamed assembly)", t));
        }
        return found;
    }

    /// <summary>Distinct enum types among the matches, optionally only from one named assembly.</summary>
    private static List<Type> DistinctTypes(IReadOnlyList<PrefabEditEnumCandidate> candidates, string? assemblyName)
    {
        var types = new List<Type>(2);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (assemblyName != null
                && !string.Equals(candidates[i].AssemblyName, assemblyName, StringComparison.Ordinal))
                continue;
            if (!types.Contains(candidates[i].Type)) types.Add(candidates[i].Type);
        }
        return types;
    }

    private static string Ambiguous(
        string fullName, IReadOnlyList<PrefabEditEnumCandidate> candidates, string? assemblyName)
    {
        var names = new List<string>(2);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (assemblyName != null
                && !string.Equals(candidates[i].AssemblyName, assemblyName, StringComparison.Ordinal))
                continue;
            if (!names.Contains(candidates[i].AssemblyName)) names.Add(candidates[i].AssemblyName);
        }
        return "ambiguous: the bit enum '" + fullName + "' is declared by "
            + names.Count.ToString(CultureInfo.InvariantCulture) + " loaded assemblies ("
            + string.Join(", ", names) + "), and Progenitor will not guess which one this component "
            + "means — name the declaring assembly in the policy, or remove the assembly that should "
            + "not be answering for a game type";
    }

    private static string SimpleName(Assembly? assembly)
    {
        try { return assembly?.GetName().Name ?? ""; }
        catch { return ""; }
    }

    private static Type? FromAssembly(Assembly? assembly, string fullName, List<string> looked)
    {
        if (assembly == null) return null;
        string name;
        try { name = assembly.GetName().Name ?? "(unnamed assembly)"; }
        catch { name = "(unnamed assembly)"; }
        looked.Add(name);
        try
        {
            var t = assembly.GetType(fullName, throwOnError: false);
            return t != null && t.IsEnum ? t : null;
        }
        catch
        {
            // A reflection-only or partially loaded assembly is skipped, never loaded.
            return null;
        }
    }

    /// <summary>The searched-assembly list for a refusal line, bounded so it stays one line.</summary>
    private static string Describe(List<string> looked)
    {
        if (looked.Count == 0) return "no loaded ProjectM/Stunlock/Unity assembly";
        const int Max = 6;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < looked.Count && i < Max; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(looked[i]);
        }
        if (looked.Count > Max)
            sb.Append(" and ").Append((looked.Count - Max).ToString(CultureInfo.InvariantCulture)).Append(" more");
        return looked.Count.ToString(CultureInfo.InvariantCulture) + " place(s): " + sb;
    }

    /// <summary>Declared member values of one enum, as the bit patterns a field read produces.</summary>
    private static long[] DeclaredMembers(Type enumType, bool unsigned)
    {
        Array values;
        try { values = Enum.GetValues(enumType); }
        catch { return Array.Empty<long>(); }
        var seen = new List<long>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            object? v = values.GetValue(i);
            if (v == null) continue;
            long bits;
            try
            {
                bits = unsigned
                    ? unchecked((long)Convert.ToUInt64(v, CultureInfo.InvariantCulture))
                    : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                continue;
            }
            if (!seen.Contains(bits)) seen.Add(bits);
        }
        return seen.ToArray();
    }

    /// <summary>Union of the members that are exactly one bit. <c>None</c> and an all-bits alias contribute nothing.</summary>
    private static long SingleBitUnion(long[] members)
    {
        long mask = 0;
        for (int i = 0; i < members.Length; i++)
        {
            long v = members[i];
            if (v == 0) continue;
            if ((v & (v - 1)) != 0) continue;
            mask |= v;
        }
        return mask;
    }

    private static bool IsUnsigned(Type t) =>
        t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) || t == typeof(char);

    /// <summary>
    /// Marshaled width of one <c>bool</c> field: what its <c>MarshalAs</c> says, else the default
    /// four-byte Win32 <c>BOOL</c>.
    /// </summary>
    public static int MarshalledBoolSize(FieldInfo? field)
    {
        try
        {
            var attr = field?.GetCustomAttribute<MarshalAsAttribute>();
            if (attr == null) return 4;
            return attr.Value switch
            {
                UnmanagedType.U1 => 1,
                UnmanagedType.I1 => 1,
                UnmanagedType.VariantBool => 2,
                _ => 4,
            };
        }
        catch
        {
            return 4;
        }
    }
}
