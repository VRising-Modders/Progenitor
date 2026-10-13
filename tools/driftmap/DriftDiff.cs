using System;
using System.Collections.Generic;
using Progenitor.Compat;

namespace Progenitor.DriftMapTool;

/// <summary>
/// Diffs two interop assembly sets and emits the <b>generated</b> half of a drift map.
///
/// Matching, in descending confidence (research compat-rewriter.md §2.2):
///
/// <list type="bullet">
/// <item><b>exact</b> — the type's full name and every member are unchanged and only the
/// assembly the TypeDef lives in moved. That is a <c>typeMove</c> and it is mechanical.</item>
/// <item><b>high</b> — a type disappeared from an assembly, exactly one type appeared there
/// with an identical member set, and neither exists anywhere else. A <c>typeRename</c>.</item>
/// <item><b>high</b> — a field disappeared and a property of the same name and type appeared on
/// the same type, or the reverse. <c>fieldToProperty</c> / <c>propertyToField</c>.</item>
/// <item><b>high</b> — inside one declaring type, exactly one member disappeared and exactly one
/// appeared with the same decoded signature, kind and static-ness. A <c>memberRename</c>. If
/// more than one candidate matches, <b>nothing</b> is emitted and the ambiguity is reported: an
/// ambiguous rename is a hand-written rule or it is not a rule.</item>
/// </list>
///
/// Two things are deliberately never generated. <c>addParam</c> — an added parameter needs a
/// human to decide which value preserves the old behavior, and getting it wrong is a silent
/// behavior change rather than a load failure (§2.2; a C# optional-parameter default cannot be
/// read out of metadata where an Il2CppInterop generator emits none). And <b>anything about a layout
/// number</b> — a changed <c>[FieldOffset]</c>, a changed struct size, a changed stride. §1.2:
/// interop regeneration already absorbs those, the plugin's IL holds no number for them, and a
/// generator that invents a rule for one is wrong. That is the S1 negative control.
/// </summary>
public static class DriftDiff
{
    /// <summary>Everything one diff produced.</summary>
    public sealed class Result
    {
        public List<DriftRule> Rules { get; } = new();

        /// <summary>Ambiguities and skips, printed by the tool and asserted by the tests.</summary>
        public List<string> Notes { get; } = new();
    }

    public static Result Compare(AssemblySurface old, AssemblySurface fresh)
    {
        var result = new Result();
        var movedOrRenamed = new HashSet<string>(StringComparer.Ordinal);

        foreach (string assembly in Sorted(old.Assemblies))
        {
            var oldTypes = old.Types(assembly);
            var newTypes = fresh.Types(assembly);

            // ---- types that left this assembly ------------------------------------------
            foreach (string type in SortedKeys(oldTypes))
            {
                if (newTypes.ContainsKey(type)) continue;

                var elsewhere = fresh.AssembliesDefining(type);
                if (elsewhere.Count == 1 && !string.Equals(elsewhere[0], assembly, StringComparison.OrdinalIgnoreCase))
                {
                    // The full name is unchanged; only the file it lives in moved.
                    if (old.AssembliesDefining(type).Contains(elsewhere[0]))
                    {
                        result.Notes.Add("typeMove skipped: " + type + " already existed in " + elsewhere[0]);
                        continue;
                    }
                    result.Rules.Add(new DriftRule(
                        DriftRule.TypeKey(assembly, type),
                        DriftRule.TypeKey(elsewhere[0], type),
                        DriftKind.TypeMove,
                        DriftConfidence.Exact,
                        DriftSource.Generated));
                    movedOrRenamed.Add(type);
                    continue;
                }
                if (elsewhere.Count > 1)
                {
                    result.Notes.Add("typeMove skipped: " + type + " now exists in "
                                     + string.Join(", ", elsewhere) + "; ambiguous, write a hand rule");
                    continue;
                }

                // Gone from the whole set: try a rename inside this assembly.
                var candidates = new List<string>();
                foreach (string candidate in SortedKeys(newTypes))
                {
                    if (oldTypes.ContainsKey(candidate)) continue;
                    if (old.AssembliesDefining(candidate).Count > 0) continue;
                    if (SameShape(oldTypes[type], newTypes[candidate])) candidates.Add(candidate);
                }
                if (candidates.Count == 1)
                {
                    result.Rules.Add(new DriftRule(
                        DriftRule.TypeKey(assembly, type),
                        DriftRule.TypeKey(assembly, candidates[0]),
                        DriftKind.TypeRename,
                        DriftConfidence.High,
                        DriftSource.Generated));
                    movedOrRenamed.Add(type);
                }
                else if (candidates.Count > 1)
                {
                    result.Notes.Add("typeRename skipped: " + type + " matches "
                                     + candidates.Count + " candidates; ambiguous, write a hand rule");
                }
                else
                {
                    result.Notes.Add("type gone: " + assembly + "!" + type + "; no rule (see the plugin author)");
                }
            }

            // ---- members of types that stayed put ---------------------------------------
            foreach (string type in SortedKeys(oldTypes))
            {
                if (!newTypes.TryGetValue(type, out var after)) continue;
                CompareMembers(assembly, oldTypes[type], after, result);
            }
        }

        return result;
    }

    private static void CompareMembers(string assembly, TypeSurface before, TypeSurface after, Result result)
    {
        var afterKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in after.Members) afterKeys.Add(member.Key);
        var beforeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in before.Members) beforeKeys.Add(member.Key);

        var gone = new List<MemberRow>();
        foreach (var member in before.Members)
            if (!member.IsCompilerGenerated && !afterKeys.Contains(member.Key)) gone.Add(member);
        var arrived = new List<MemberRow>();
        foreach (var member in after.Members)
            if (!member.IsCompilerGenerated && !beforeKeys.Contains(member.Key)) arrived.Add(member);
        if (gone.Count == 0 && arrived.Count == 0) return;

        // 1) field <-> property flips first, and they consume both sides of the pool. A backing
        //    field carries a compiler-generated name and was already excluded, but the accessor
        //    pair must be taken out here or the rename pass would read "one field gone, one
        //    method arrived" and invent a rename that is really this flip.
        FlipFieldToProperty(assembly, before, after, gone, arrived, result);
        FlipPropertyToField(assembly, before, after, gone, arrived, result);

        // 2) renames: one-to-one inside one signature bucket, same kind and static-ness.
        Rename(assembly, before.FullName, gone, arrived, result);
    }

    private static void FlipFieldToProperty(
        string assembly, TypeSurface before, TypeSurface after,
        List<MemberRow> gone, List<MemberRow> arrived, Result result)
    {
        for (int i = gone.Count - 1; i >= 0; i--)
        {
            var field = gone[i];
            if (!field.IsField) continue;
            var getter = Find(arrived, "get_" + field.Name, field.Signature + "()", isField: false);
            if (getter == null) continue;
            result.Rules.Add(new DriftRule(
                DriftRule.Key(assembly, before.FullName, field.Name, field.Signature),
                DriftRule.Key(assembly, after.FullName, field.Name, field.Signature),
                DriftKind.FieldToProperty,
                DriftConfidence.High,
                DriftSource.Generated,
                "field became a property; ldfld/stfld become call get_/set_"));
            gone.RemoveAt(i);
            arrived.Remove(getter);
            var setter = Find(arrived, "set_" + field.Name, "System.Void(" + field.Signature + ")", isField: false);
            if (setter != null) arrived.Remove(setter);
        }
    }

    private static void FlipPropertyToField(
        string assembly, TypeSurface before, TypeSurface after,
        List<MemberRow> gone, List<MemberRow> arrived, Result result)
    {
        for (int i = gone.Count - 1; i >= 0; i--)
        {
            var getter = gone[i];
            if (getter.IsField || !getter.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
            string property = getter.Name.Substring(4);
            int open = getter.Signature.IndexOf('(');
            if (open <= 0 || !getter.Signature.EndsWith("()", StringComparison.Ordinal)) continue;
            string type = getter.Signature.Substring(0, open);
            var field = Find(arrived, property, type, isField: true);
            if (field == null) continue;
            string to = DriftRule.Key(assembly, after.FullName, property, type);
            result.Rules.Add(new DriftRule(
                DriftRule.Key(assembly, before.FullName, getter.Name, getter.Signature),
                to, DriftKind.PropertyToField, DriftConfidence.High, DriftSource.Generated,
                "property became a field; call get_ becomes ldfld"));
            gone.RemoveAt(i);
            arrived.Remove(field);
            var setter = Find(gone, "set_" + property, "System.Void(" + type + ")", isField: false);
            if (setter != null)
            {
                result.Rules.Add(new DriftRule(
                    DriftRule.Key(assembly, before.FullName, setter.Name, setter.Signature),
                    to, DriftKind.PropertyToField, DriftConfidence.High, DriftSource.Generated,
                    "property became a field; call set_ becomes stfld"));
                gone.Remove(setter);
            }
        }
    }

    private static void Rename(
        string assembly, string type, List<MemberRow> gone, List<MemberRow> arrived, Result result)
    {
        foreach (var member in gone)
        {
            var candidates = new List<MemberRow>();
            foreach (var candidate in arrived)
                if (candidate.IsField == member.IsField
                    && candidate.IsStatic == member.IsStatic
                    && string.Equals(candidate.Signature, member.Signature, StringComparison.Ordinal))
                    candidates.Add(candidate);
            if (candidates.Count == 0)
            {
                result.Notes.Add("member gone: " + assembly + "!" + type + "." + member.Name
                                 + " " + member.Signature + "; no rule (see the plugin author)");
                continue;
            }
            if (candidates.Count > 1)
            {
                result.Notes.Add("memberRename skipped: " + assembly + "!" + type + "." + member.Name
                                 + " matches " + candidates.Count + " candidates; ambiguous, write a hand rule");
                continue;
            }
            // One-to-one only: the reverse direction must be unambiguous too.
            int reverse = 0;
            foreach (var other in gone)
                if (other.IsField == member.IsField
                    && other.IsStatic == member.IsStatic
                    && string.Equals(other.Signature, member.Signature, StringComparison.Ordinal))
                    reverse++;
            if (reverse > 1)
            {
                result.Notes.Add("memberRename skipped: " + assembly + "!" + type + "." + member.Name
                                 + " is one of " + reverse + " members with the same signature; ambiguous");
                continue;
            }
            // R33 finding 7: a signature match on its own is not evidence of a rename. One
            // removal plus one unrelated addition with the same signature in one type is an
            // ordinary shape for a build diff (`EnableDebugDraw(int)` goes, `SetSpawnBudget(int)`
            // arrives), and High is applied without review — so that pairing would silently
            // redirect every call to a method that compiles, binds, runs and does something else.
            // Require a second piece of evidence: the names have to look like the same member.
            // Anything else is still emitted, as Heuristic, which is reported and never applied
            // and which an operator can promote to a hand rule after looking at it. This pass
            // invents the target rather than being given one, so it has to earn the confidence.
            bool named = NamesLookLikeARename(member.Name, candidates[0].Name);
            if (!named)
                result.Notes.Add("memberRename demoted to heuristic: " + assembly + "!" + type + "."
                                 + member.Name + " -> " + candidates[0].Name
                                 + "; the signatures match but the names share nothing. Review it and, if it is"
                                 + " right, promote it to a hand rule.");
            result.Rules.Add(new DriftRule(
                DriftRule.Key(assembly, type, member.Name, member.Signature),
                DriftRule.Key(assembly, type, candidates[0].Name, candidates[0].Signature),
                DriftKind.MemberRename,
                named ? DriftConfidence.High : DriftConfidence.Heuristic,
                DriftSource.Generated,
                named ? "" : "signature-only match; no name evidence"));
        }
    }

    /// <summary>
    /// How alike two member names have to be before a signature-only match is called a rename.
    /// 0.5 keeps <c>Ping</c>/<c>Pong</c> (one edit in four characters) and rejects
    /// <c>EnableDebugDraw</c>/<c>SetSpawnBudget</c>.
    /// </summary>
    public const double RenameSimilarityFloor = 0.5;

    /// <summary>
    /// True when the two names share a stem: one contains the other, they share a leading run of
    /// at least four characters, or their edit distance is small relative to their length. This
    /// is deliberately generous — it only ever decides <b>High versus Heuristic</b>, never
    /// whether a rule is emitted, so a false negative costs a review and a false positive is the
    /// failure this check exists to make rare.
    /// </summary>
    public static bool NamesLookLikeARename(string? before, string? after)
    {
        string a = before ?? "";
        string b = after ?? "";
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        int shared = 0;
        while (shared < a.Length && shared < b.Length
               && char.ToLowerInvariant(a[shared]) == char.ToLowerInvariant(b[shared])) shared++;
        if (shared >= 4) return true;

        int distance = EditDistance(a, b);
        int longest = Math.Max(a.Length, b.Length);
        return longest > 0 && 1.0 - ((double)distance / longest) >= RenameSimilarityFloor;
    }

    /// <summary>Levenshtein distance, case-insensitive. Two rows, so no allocation per cell.</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            Array.Copy(current, previous, current.Length);
        }
        return previous[b.Length];
    }

    private static MemberRow? Find(List<MemberRow> rows, string name, string signature, bool isField)
    {
        foreach (var row in rows)
            if (row.IsField == isField
                && string.Equals(row.Name, name, StringComparison.Ordinal)
                && string.Equals(row.Signature, signature, StringComparison.Ordinal))
                return row;
        return null;
    }

    private static bool SameShape(TypeSurface a, TypeSurface b)
    {
        var left = a.Shape();
        var right = b.Shape();
        if (left.Count == 0 || left.Count != right.Count) return false;
        foreach (string key in left)
            if (!right.Contains(key)) return false;
        return true;
    }

    private static List<string> Sorted(IEnumerable<string> values)
    {
        var list = new List<string>(values);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static List<string> SortedKeys(IReadOnlyDictionary<string, TypeSurface> types)
    {
        var list = new List<string>(types.Keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
