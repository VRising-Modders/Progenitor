using System;
using System.Collections.Generic;
using Progenitor.Compat;

namespace Progenitor.Resilience;

/// <summary>One interop assembly that is present in both fingerprints but not byte-identical.</summary>
public sealed class InteropChange
{
    public InteropChange(AssemblyFingerprint before, AssemblyFingerprint after)
    {
        Before = before;
        After = after;
        Description = after?.DescribeChangeFrom(before!) ?? "";
    }

    public AssemblyFingerprint Before { get; }
    public AssemblyFingerprint After { get; }

    /// <summary>e.g. <c>size 12,873,216 -&gt; 12,901,888, sha 4f2a… -&gt; 91cd…</c>.</summary>
    public string Description { get; }

    public string Name => After?.Name ?? Before?.Name ?? "";
}

/// <summary>
/// Pure comparison of two <see cref="BuildFingerprint"/>s. No file system, no game, no clock —
/// it takes two objects and answers "what moved, and what of ours breaks". Everything the
/// startup log block and <c>tools/resilience-report.ps1</c> print comes from here, which is why
/// the tests can drive it with fabricated fingerprints.
/// </summary>
public sealed class ResilienceDiff
{
    private ResilienceDiff() { }

    /// <summary>False when there was no stored fingerprint to compare against (first ever run).</summary>
    public bool HasBaseline { get; private set; }

    /// <summary>File the baseline came from, for the log line. May be "".</summary>
    public string BaselineFile { get; private set; } = "";

    public string BaselineCapturedUtc { get; private set; } = "";

    /// <summary>Baseline <c>{side}-{build}</c>, "" without a baseline.</summary>
    public string PreviousKey { get; private set; } = "";

    public string CurrentKey { get; private set; } = "";

    /// <summary>The game binary's address-library key changed: a game update, or a different branch.</summary>
    public bool BuildChanged { get; private set; }

    /// <summary>
    /// GameAssembly.dll's full hash changed while the build id did not. The build id only hashes
    /// the first 64 KiB (see <c>AddressId.BuildIdPrefixBytes</c>), so this is the case the id
    /// cannot see on its own: a patch that left the PE header, size and first 64 KiB alone.
    /// Every relative address in the address table is suspect when this is true.
    /// </summary>
    public bool GameAssemblyChangedUnderSameBuildId { get; private set; }

    public IReadOnlyList<AssemblyFingerprint> AddedInterop { get; private set; } = Array.Empty<AssemblyFingerprint>();
    public IReadOnlyList<AssemblyFingerprint> RemovedInterop { get; private set; } = Array.Empty<AssemblyFingerprint>();
    public IReadOnlyList<InteropChange> ChangedInterop { get; private set; } = Array.Empty<InteropChange>();

    /// <summary>No <c>{side}-{build}.json</c> address table exists for the build now running.</summary>
    public bool AddressTableMissing { get; private set; }

    /// <summary>Detour sites with no table entry now. With no table at all, that is every site.</summary>
    public IReadOnlyList<string> DetourSitesWithoutEntry { get; private set; } = Array.Empty<string>();

    /// <summary>Detour sites that had a table entry in the baseline and have none now.</summary>
    public IReadOnlyList<string> DetourSitesLost { get; private set; } = Array.Empty<string>();

    /// <summary>References that bound in the baseline and do not bind now. The breakage list.</summary>
    public IReadOnlyList<MemberBinding> MembersLost { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>References that did not bind before and bind now (a fixed table or a rebuilt Core).</summary>
    public IReadOnlyList<MemberBinding> MembersRegained { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>References Core no longer makes (Core itself was rebuilt between the two runs).</summary>
    public IReadOnlyList<MemberBinding> MembersDropped { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>References Core makes now and did not before (Core itself was rebuilt).</summary>
    public IReadOnlyList<MemberBinding> MembersNew { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>Rows that do not bind right now, whatever the baseline said.</summary>
    public IReadOnlyList<MemberBinding> MembersUnbound { get; private set; } = Array.Empty<MemberBinding>();

    // ---- the join with the compatibility rewriter (job FCOMPAT3) ---------------------------
    //
    // The scan says what broke; the drift map says what of it can be absorbed. Both are keyed on
    // the same `Assembly|Type|Name|Signature` string — MemberBinding.Key and DriftRule.From are
    // literally the same encoding, which is why the join is string equality and needs no second
    // identity scheme. It is pure: a diff, a map, no file system, no game, no clock.

    /// <summary>
    /// Where the drift map for the running build comes from. Null (the default, and the whole
    /// state of the world for the tests and the offline tool) means "no map": the join then puts
    /// every unbound row in <see cref="MembersNeedingAuthor"/> and the report is unchanged.
    /// <c>Compat.CompatResilienceJoin.Install</c> points it at the running build's map.
    /// </summary>
    public static Func<DriftMap?>? DriftMapProvider;

    /// <summary>True when a drift map for this build was joined in.</summary>
    public bool HasDriftMap { get; private set; }

    /// <summary>The map's log line (name, hash, rule counts), or "".</summary>
    public string DriftMapLine { get; private set; } = "";

    /// <summary>
    /// Unbound rows the current build's drift map has an applicable rule for: a rewrite would
    /// fix these, and with <c>[Compat] RewriteMode = Apply</c> and a vetted map it already does.
    /// </summary>
    public IReadOnlyList<MemberBinding> MembersFixableByRewrite { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>Unbound rows no rule covers. These need the plugin author, not a map.</summary>
    public IReadOnlyList<MemberBinding> MembersNeedingAuthor { get; private set; } = Array.Empty<MemberBinding>();

    /// <summary>
    /// Split <see cref="MembersUnbound"/> into "a rule fixes this" and "a human has to". A
    /// member row joins on its own key first; when the map only carries a rule for the declaring
    /// <b>type</b> (a typeRename or typeMove), the member follows the type, so the type key is
    /// the fallback. Only <see cref="DriftRule.IsApplicable"/> rules count — a heuristic or an
    /// <c>addParam</c> rule is reported but never applied, so promising it as a fix would be a
    /// lie the report cannot cash.
    /// </summary>
    public ResilienceDiff JoinDriftMap(DriftMap? map)
    {
        HasDriftMap = map != null;
        DriftMapLine = map == null ? "" : DriftMapStore.FormatMapLine(map, 0);
        if (map == null)
        {
            MembersFixableByRewrite = Array.Empty<MemberBinding>();
            MembersNeedingAuthor = MembersUnbound;
            return this;
        }

        var fixable = new List<MemberBinding>();
        var author = new List<MemberBinding>();
        foreach (var m in MembersUnbound)
        {
            if (Fixes(map, m)) fixable.Add(m); else author.Add(m);
        }
        MemberBinding.Sort(fixable);
        MemberBinding.Sort(author);
        MembersFixableByRewrite = fixable;
        MembersNeedingAuthor = author;
        return this;
    }

    private static bool Fixes(DriftMap map, MemberBinding row)
    {
        if (row == null) return false;
        var rule = map.Find(row.Key);
        if (rule != null && rule.IsApplicable) return true;
        var typeRule = map.Find(DriftRule.TypeKey(row.Assembly, row.Type));
        return typeRule != null && typeRule.IsApplicable && typeRule.IsTypeRule;
    }

    private static DriftMap? Map()
    {
        var provider = DriftMapProvider;
        if (provider == null) return null;
        try { return provider(); }
        catch { return null; }
    }

    public int MemberCount { get; private set; }

    /// <summary>Rows the scan could not check (missing assembly, irreducible TypeSpec parent).</summary>
    public int MembersNotChecked { get; private set; }

    /// <summary>Rows that were actually resolved against an assembly on disk.</summary>
    public int MembersChecked => MemberCount - MembersNotChecked;

    /// <summary>
    /// There are references and not one of them was checked — the interop assemblies could not
    /// be read at all (renamed <c>ProjectM.dll</c>, a regenerated interop folder). That is the
    /// exact failure the member check exists to catch, so it is neither "unchanged" nor "all
    /// bind": <see cref="Unchanged"/> is false and <see cref="HasBreakage"/> is true (R31
    /// finding 11).
    /// </summary>
    public bool NothingChecked => MemberCount > 0 && MembersChecked == 0;

    /// <summary>Why the unchecked rows were unchecked (the first reason a row carried), or "".</summary>
    public string NotCheckedReason { get; private set; } = "";

    /// <summary>
    /// Core itself is not the Core the baseline was taken under: its assembly version moved, or
    /// its module version id did (job FUPD10, R57 finding 7).
    /// </summary>
    /// <remarks>
    /// The version alone was the whole test until FUPD10, and a developer-tree deploy never moves
    /// it — version bumps are a separate commit by project rule — so <c>CoreChanged</c> was false
    /// on exactly the deploys that moved the reference count, and the <c>core</c> row FUPD9 fired
    /// in its place could name no Core at all. The MVID moves on every compilation. Both sides
    /// must have recorded one for the MVID half to count: a baseline stored before this field
    /// existed records "", which is "cannot say", not "changed".
    /// </remarks>
    public bool CoreChanged { get; private set; }

    /// <summary>The baseline's Core assembly version, "" without a baseline.</summary>
    public string PreviousCoreVersion { get; private set; } = "";

    /// <summary>The baseline's Core MVID, "" without a baseline or from a pre-FUPD10 fingerprint.</summary>
    public string PreviousCoreMvid { get; private set; } = "";

    /// <summary><c>0.1.0.0/a1b2c3d4</c> for the baseline's Core, for the <c>core</c> row's left half.</summary>
    public string PreviousCoreIdentity =>
        BuildFingerprint.FormatCoreIdentity(PreviousCoreVersion, PreviousCoreMvid);

    /// <summary>
    /// Something about <b>the game</b> moved: its build id, its bytes, its interop set, the
    /// address table, or a reference that used to bind and no longer does. This is the axis the
    /// report's headline is about, and it deliberately excludes every fact that is only about
    /// Core — <see cref="MembersNew"/>, <see cref="MembersDropped"/> and
    /// <see cref="CoreChanged"/> — because a Core deploy that touches a new game type changes
    /// none of the game (job FUPD8, R55 finding 5: the test server printed "game build CHANGED"
    /// with every row saying unchanged, because Core's own reference set grew from 890 to 895).
    /// </summary>
    public bool GameChanged =>
        HasBaseline
        && (BuildChanged
            || GameAssemblyChangedUnderSameBuildId
            || AddedInterop.Count > 0
            || RemovedInterop.Count > 0
            || ChangedInterop.Count > 0
            || AddressTableMissing
            || NothingChecked
            || DetourSitesLost.Count > 0
            || MembersLost.Count > 0);

    /// <summary>
    /// True when something a plugin author would want to know about is broken <b>right now</b>,
    /// whatever the baseline said: an address table that is absent, a detour site with no entry
    /// in the table that is present, a reference that does not bind, or a member scan that
    /// checked nothing. This is the list <c>tools/resilience-report.ps1</c> turns into exit 1.
    /// </summary>
    public bool HasBreakage =>
        AddressTableMissing || DetourSitesWithoutEntry.Count > 0 || MembersUnbound.Count > 0
        || NothingChecked;

    /// <summary>
    /// True when there is a baseline, the game did not move, and nothing of ours is broken. This
    /// is the "one line" case. Core's reference set may have grown or shrunk since the baseline —
    /// that is reported as a detail on the <c>members</c> row and never as a verdict — but
    /// anything <see cref="HasBreakage"/> counts drops out of this case and gets the full block
    /// with its detail rows.
    /// <para>
    /// The second clause is <c>!HasBreakage</c> and not a re-listing of its terms, so that
    /// <b>Unchanged implies !HasBreakage by construction</b> and a term added to one can never be
    /// forgotten in the other. Job FUPD8 re-listed one of the four terms
    /// (<see cref="MembersUnbound"/>) and left <see cref="DetourSitesWithoutEntry"/> behind, so a
    /// site missing from a table that is otherwise present satisfied both predicates at once: the
    /// one-line block headlined "game build unchanged", and the offline tool exiting 1 over it,
    /// with no detail row naming the site (job FUPD9, R56 finding 4).
    /// </para>
    /// </summary>
    public bool Unchanged => HasBaseline && !GameChanged && !HasBreakage;

    /// <summary>
    /// Compare a stored fingerprint (may be null: first run) with the one just captured.
    /// </summary>
    public static ResilienceDiff Compare(
        BuildFingerprint? baseline,
        BuildFingerprint current,
        string baselineFile = "")
    {
        if (current == null) throw new ArgumentNullException(nameof(current));

        var diff = new ResilienceDiff
        {
            HasBaseline = baseline != null,
            BaselineFile = baselineFile ?? "",
            BaselineCapturedUtc = baseline?.CapturedUtc ?? "",
            PreviousKey = baseline?.Key ?? "",
            CurrentKey = current.Key,
            AddressTableMissing = !current.AddressTablePresent,
            DetourSitesWithoutEntry = current.DetourSitesMissing,
            MemberCount = current.Members.Count,
        };

        if (baseline != null)
        {
            diff.BuildChanged = !string.Equals(baseline.Key, current.Key, StringComparison.Ordinal);
            diff.PreviousCoreVersion = baseline.CoreVersion;
            diff.PreviousCoreMvid = baseline.CoreMvid;
            diff.CoreChanged =
                !string.Equals(baseline.CoreVersion, current.CoreVersion, StringComparison.Ordinal)
                || (baseline.CoreMvid.Length > 0
                    && current.CoreMvid.Length > 0
                    && !string.Equals(baseline.CoreMvid, current.CoreMvid, StringComparison.OrdinalIgnoreCase));
            diff.GameAssemblyChangedUnderSameBuildId =
                !diff.BuildChanged
                && baseline.GameAssemblySha256.Length > 0
                && current.GameAssemblySha256.Length > 0
                && !string.Equals(baseline.GameAssemblySha256, current.GameAssemblySha256, StringComparison.OrdinalIgnoreCase);
            DiffInterop(baseline, current, diff);
            DiffDetourSites(baseline, current, diff);
        }

        DiffMembers(baseline, current, diff);
        // The rewriter half of the answer, when a map for this build is loaded. Never a
        // decision: nothing here refuses a load, and a missing map leaves the block as it was.
        diff.JoinDriftMap(Map());
        return diff;
    }

    private static void DiffInterop(BuildFingerprint baseline, BuildFingerprint current, ResilienceDiff diff)
    {
        var before = new Dictionary<string, AssemblyFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in baseline.Interop) before[a.Name] = a;

        var added = new List<AssemblyFingerprint>();
        var changed = new List<InteropChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in current.Interop)
        {
            seen.Add(a.Name);
            if (!before.TryGetValue(a.Name, out var old)) { added.Add(a); continue; }
            if (!a.SameAs(old)) changed.Add(new InteropChange(old, a));
        }

        var removed = new List<AssemblyFingerprint>();
        foreach (var a in baseline.Interop)
            if (!seen.Contains(a.Name)) removed.Add(a);

        added.Sort(ByName);
        removed.Sort(ByName);
        changed.Sort(static (x, y) => string.CompareOrdinal(x.Name, y.Name));

        diff.AddedInterop = added;
        diff.RemovedInterop = removed;
        diff.ChangedInterop = changed;
    }

    private static int ByName(AssemblyFingerprint a, AssemblyFingerprint b) =>
        string.CompareOrdinal(a?.Name ?? "", b?.Name ?? "");

    private static void DiffDetourSites(BuildFingerprint baseline, BuildFingerprint current, ResilienceDiff diff)
    {
        var now = new HashSet<string>(current.DetourSitesInTable, StringComparer.Ordinal);
        var lost = new List<string>();
        foreach (var site in baseline.DetourSitesInTable)
            if (!now.Contains(site)) lost.Add(site);
        lost.Sort(StringComparer.Ordinal);
        diff.DetourSitesLost = lost;
    }

    private static void DiffMembers(BuildFingerprint? baseline, BuildFingerprint current, ResilienceDiff diff)
    {
        var unbound = new List<MemberBinding>();
        int notChecked = 0;
        string reason = "";
        foreach (var m in current.Members)
        {
            if (m.Status == BindingStatus.NotChecked)
            {
                notChecked++;
                if (reason.Length == 0 && m.Found.Length > 0) reason = m.Found;
                continue;
            }
            if (m.Status != BindingStatus.Bound) unbound.Add(m);
        }
        unbound.Sort(MemberBinding.Compare);
        diff.MembersUnbound = unbound;
        diff.MembersNotChecked = notChecked;
        diff.NotCheckedReason = reason;

        if (baseline == null) return;

        var before = new Dictionary<string, MemberBinding>(StringComparer.Ordinal);
        foreach (var m in baseline.Members) before[m.Key] = m;

        var lost = new List<MemberBinding>();
        var regained = new List<MemberBinding>();
        var added = new List<MemberBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in current.Members)
        {
            seen.Add(m.Key);
            if (!before.TryGetValue(m.Key, out var old)) { added.Add(m); continue; }
            bool wasBound = old.Status == BindingStatus.Bound;
            bool isBound = m.Status == BindingStatus.Bound;
            if (wasBound && !isBound) lost.Add(m);
            else if (!wasBound && isBound) regained.Add(m);
        }

        var dropped = new List<MemberBinding>();
        foreach (var m in baseline.Members)
            if (!seen.Contains(m.Key)) dropped.Add(m);

        MemberBinding.Sort(lost);
        MemberBinding.Sort(regained);
        MemberBinding.Sort(added);
        MemberBinding.Sort(dropped);

        diff.MembersLost = lost;
        diff.MembersRegained = regained;
        diff.MembersNew = added;
        diff.MembersDropped = dropped;
    }
}
