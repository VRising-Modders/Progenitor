using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Native;

namespace Progenitor.Resilience;

/// <summary>
/// Turns a <see cref="ResilienceDiff"/> into the one readable block that goes to the log at
/// startup and to stdout from <c>tools/resilience-report.ps1</c>. Formatting only: no I/O, no
/// decisions. Every line is prefixed <c>Resilience:</c> or indented under it so a single grep
/// pulls the whole block out of a BepInEx log.
///
/// The block is a report, never a gate. Nothing in Progenitor reads it back to refuse a load.
/// </summary>
public static class ResilienceReport
{
    public const string Prefix = "Resilience:";

    /// <summary>Detail rows printed per section before "… N more".</summary>
    public const int MaxDetailLines = 8;

    /// <summary>The unchanged-build headline. Also the string the tests pin.</summary>
    public const string UnchangedHeadline = Prefix + " game build unchanged";

    /// <summary>The changed-build headline.</summary>
    public const string ChangedHeadline = Prefix + " game build CHANGED";

    /// <summary>
    /// The headline for the one case that is breakage without the game having moved: Core
    /// references something this build does not have. Deliberately not a prefix of
    /// <see cref="UnchangedHeadline"/> or <see cref="ChangedHeadline"/>, so a grep — or a test
    /// asserting one of those — cannot mistake the three for each other (job FUPD8).
    /// </summary>
    public const string ReferencesBreakageHeadline =
        Prefix + " references DO NOT bind (game build unchanged)";

    /// <summary>
    /// The other breakage-without-a-game-move case: the address table for this build is present
    /// but a detour site has no entry in it. Before job FUPD9 this case reached the one-line
    /// "game build unchanged" block — with the whole story compressed into an <c>addresslib</c>
    /// row reading <c>1/2</c> — while <see cref="ResilienceDiff.HasBreakage"/> was true and the
    /// offline tool exited 1 over that text (R56 finding 4).
    /// </summary>
    public const string DetourSitesBreakageHeadline =
        Prefix + " detour sites HAVE NO ENTRY (game build unchanged)";

    /// <summary>Closing line: says out loud that nothing was blocked.</summary>
    public const string Footer = "  note        report only - nothing was blocked, plugins load as usual";

    /// <summary>
    /// Format the whole block. Never throws; a null diff yields a single line.
    /// <paramref name="archive"/> (job FUPD4) is the interop-archive step's outcome; null leaves
    /// the block exactly as it was before that step existed.
    /// </summary>
    /// <param name="store">
    /// Job FUPD5: the folder the fingerprint is written to and how it was resolved
    /// (<c>ResilienceCheck.FormatStoreLine</c>). Null or "" omits the row, which is what a caller
    /// that formats a diff on its own gets. The row exists because the folder was silently empty
    /// in the game for the whole of M4.1 and the block had no way to say so.
    /// </param>
    public static string Format(
        ResilienceDiff? diff,
        BuildFingerprint? current,
        InteropArchiveResult? archive = null,
        string? store = null)
    {
        if (diff == null || current == null)
            return Prefix + " no report (fingerprint unavailable)";

        var sb = new StringBuilder();
        if (diff.Unchanged)
        {
            sb.Append(UnchangedHeadline).Append('\n');
            Line(sb, "build", current.Key);
            Line(sb, "baseline", Baseline(diff));
            AppendStore(sb, store);
            Line(sb, "interop", current.Interop.Count.ToString(CultureInfo.InvariantCulture) + " assemblies, unchanged");
            Line(sb, "addresslib", TableLine(current));
            AppendArchive(sb, archive);
            Line(sb, "members", MembersUnchangedLine(diff));
            AppendCore(sb, diff, current);
            return sb.ToString().TrimEnd('\n');
        }

        sb.Append(diff.HasBaseline ? Headline(diff) : Prefix + " first run, no stored fingerprint")
            .Append('\n');
        Line(sb, "build", diff.HasBaseline && diff.BuildChanged
            ? current.Key + "  (was " + diff.PreviousKey + ")"
            : current.Key);
        // Not "storing X": the offline tool's -NoWrite run must not claim a write it did not make.
        Line(sb, "baseline", diff.HasBaseline ? Baseline(diff) : "none - this run's fingerprint is " + current.FileName);
        AppendStore(sb, store);

        if (diff.GameAssemblyChangedUnderSameBuildId)
            Line(sb, "gameasm", "GameAssembly.dll bytes changed under the SAME build id - every relative address is suspect");

        AppendInterop(sb, diff, current);
        AppendArchive(sb, archive);
        AppendAddressLib(sb, diff, current);
        AppendMembers(sb, diff, current);
        AppendCore(sb, diff, current);
        sb.Append(Footer);
        return sb.ToString();
    }

    /// <summary>
    /// Which of the three verdicts this block carries. The game moving wins; otherwise the
    /// headline names the thing that is broken, because <see cref="ResilienceDiff.Unchanged"/>
    /// is now exactly "a baseline, no game move, and no breakage" (job FUPD9) and the two
    /// remaining breakage terms read very differently to an operator.
    /// </summary>
    private static string Headline(ResilienceDiff diff)
    {
        if (diff.GameChanged) return ChangedHeadline;
        if (diff.MembersUnbound.Count > 0) return ReferencesBreakageHeadline;
        if (diff.DetourSitesWithoutEntry.Count > 0) return DetourSitesBreakageHeadline;
        // Unreachable while the two remaining breakage terms (AddressTableMissing,
        // NothingChecked) are also GameChanged terms. If a future term breaks that, the loud
        // verdict is the safe one: a block that is here at all is not the one-line case.
        return ChangedHeadline;
    }

    /// <summary>
    /// The <c>core</c> row: why the <c>members</c> row's "+5 since baseline" is there, and
    /// <b>which Core</b> it is there under.
    /// </summary>
    /// <remarks>
    /// <para>Job FUPD8 keyed the row on <see cref="ResilienceDiff.CoreChanged"/>, an
    /// assembly-version comparison — and a developer-tree deploy does not bump the version
    /// (version bumps are a separate commit by project rule), so the row was false on exactly the
    /// deploys that move the count: the test server printed <c>897 references (+2 since
    /// baseline)</c> with no row saying under which Core (R56 finding 9). Job FUPD9 fired the row
    /// anyway, and printed <c>current.CoreVersion</c> — which on that branch is by construction
    /// the version the baseline already carried, so the row announced a moved reference set and
    /// still identified no Core (R57 finding 7).</para>
    /// <para>Job FUPD10: the fingerprint records Core's MVID beside its version
    /// (<see cref="BuildFingerprint.CoreMvid"/>), <see cref="ResilienceDiff.CoreChanged"/> is
    /// true when either moved, and the row prints both ends of the move. The second branch — a
    /// reference set that moved under an identity that did not — is now only reachable when one
    /// of the two fingerprints could not read Core at all, and it says the count rather than
    /// repeating the fact, because "which Core" is the question it cannot answer.</para>
    /// </remarks>
    private static void AppendCore(StringBuilder sb, ResilienceDiff diff, BuildFingerprint current)
    {
        if (diff.CoreChanged)
        {
            Line(sb, "core", "Progenitor " + diff.PreviousCoreIdentity + " -> " + current.CoreIdentity);
            return;
        }

        if (diff.MembersNew.Count == 0 && diff.MembersDropped.Count == 0) return;
        Line(sb, "core", "Core references moved under the same " + current.CoreIdentity
                         + " (+" + diff.MembersNew.Count + "/-" + diff.MembersDropped.Count + ")");
    }

    /// <summary>
    /// The one row that says whether this build's interop set is still diffable. It is worth a
    /// line even when it is boring, because the day it says NOT archived is the day the next
    /// game update becomes un-analyzable, and nothing else in the log would mention it.
    /// </summary>
    private static void AppendArchive(StringBuilder sb, InteropArchiveResult? archive)
    {
        if (archive == null) return;
        // Job FUPD6: the archive ships off, so "off" is now a row worth printing — but only when
        // it can say why. A caller that simply did not ask for one keeps the block it always had.
        if (archive.Status == InteropArchiveStatus.Disabled && archive.Reason.Length == 0) return;
        Line(sb, "archive", InteropArchive.DescribeStatus(archive));
    }

    /// <summary>
    /// Where the fingerprint goes, and how that folder was decided. Printed next to
    /// <c>baseline</c> because the two answer the same question: which file this run compares
    /// against, and where the next run will find it.
    /// </summary>
    private static void AppendStore(StringBuilder sb, string? store)
    {
        if (string.IsNullOrEmpty(store)) return;
        Line(sb, "store", store!);
    }

    private static void AppendInterop(StringBuilder sb, ResilienceDiff diff, BuildFingerprint current)
    {
        if (!diff.HasBaseline)
        {
            Line(sb, "interop", current.Interop.Count.ToString(CultureInfo.InvariantCulture)
                                + " assemblies recorded from " + current.InteropDirectory);
            return;
        }

        int changes = diff.AddedInterop.Count + diff.RemovedInterop.Count + diff.ChangedInterop.Count;
        if (changes == 0)
        {
            Line(sb, "interop", current.Interop.Count.ToString(CultureInfo.InvariantCulture) + " assemblies, unchanged");
            return;
        }

        Line(sb, "interop", current.Interop.Count.ToString(CultureInfo.InvariantCulture) + " assemblies: "
            + diff.AddedInterop.Count + " added, " + diff.RemovedInterop.Count + " removed, "
            + diff.ChangedInterop.Count + " changed");

        int shown = 0;
        foreach (var a in diff.AddedInterop)
        {
            if (shown >= MaxDetailLines) break;
            Detail(sb, "added    " + a.Name + (a.Version.Length == 0 ? "" : " " + a.Version));
            shown++;
        }
        foreach (var a in diff.RemovedInterop)
        {
            if (shown >= MaxDetailLines) break;
            Detail(sb, "removed  " + a.Name + (a.Version.Length == 0 ? "" : " " + a.Version));
            shown++;
        }
        foreach (var c in diff.ChangedInterop)
        {
            if (shown >= MaxDetailLines) break;
            Detail(sb, "changed  " + c.Name + (c.Description.Length == 0 ? "" : "  " + c.Description));
            shown++;
        }
        if (changes > shown) Detail(sb, "... " + (changes - shown) + " more");
    }

    private static void AppendAddressLib(StringBuilder sb, ResilienceDiff diff, BuildFingerprint current)
    {
        if (diff.AddressTableMissing)
        {
            Line(sb, "addresslib", "NO TABLE for this build (" + current.AddressTableFileName + ") - "
                + current.DetourSitesMissing.Count + " detour site(s) will not resolve from the table");
            foreach (var site in Take(current.DetourSitesMissing)) Detail(sb, "no entry  " + site);
            Detail(sb, "rebuild   " + AddressId.FormatRebuildCommand(current.Side));
            return;
        }

        Line(sb, "addresslib", TableLine(current));
        foreach (var site in Take(diff.DetourSitesLost)) Detail(sb, "lost entry " + site);
        foreach (var site in Take(current.DetourSitesMissing)) Detail(sb, "no entry  " + site);
        if (current.DetourSitesMissing.Count > 0)
            Detail(sb, "rebuild   " + AddressId.FormatRebuildCommand(current.Side));
    }

    private static void AppendMembers(StringBuilder sb, ResilienceDiff diff, BuildFingerprint current)
    {
        if (diff.MemberCount == 0)
        {
            Line(sb, "members", "not scanned");
            return;
        }

        if (diff.NothingChecked)
        {
            Line(sb, "members", NothingCheckedLine(diff));
            if (diff.NotCheckedReason.Length > 0) Detail(sb, "why       " + Trim(diff.NotCheckedReason));
            return;
        }

        var parts = new List<string> { ReferenceCount(diff) };
        if (diff.MembersUnbound.Count > 0)
        {
            parts.Add(diff.MembersUnbound.Count + " do not bind");
            // Job FCOMPAT3: with a drift map loaded, say which half of the breakage the rewriter
            // can absorb. Without one the line is exactly what it always was.
            if (diff.HasDriftMap)
            {
                parts.Add(diff.MembersFixableByRewrite.Count + " fixable by the drift map");
                parts.Add(diff.MembersNeedingAuthor.Count + " need the plugin author");
            }
        }
        else parts.Add(BindVerdict(diff));
        if (diff.MembersLost.Count > 0) parts.Add(diff.MembersLost.Count + " lost since baseline");
        if (diff.MembersRegained.Count > 0) parts.Add(diff.MembersRegained.Count + " regained");
        if (diff.MembersNotChecked > 0) parts.Add(diff.MembersNotChecked + " not checked");
        Line(sb, "members", string.Join(", ", parts));
        if (diff.HasDriftMap) Line(sb, "rewrite", diff.DriftMapLine);

        var fixable = new HashSet<string>(StringComparer.Ordinal);
        if (diff.HasDriftMap)
            foreach (var m in diff.MembersFixableByRewrite) fixable.Add(m.Key);

        var show = diff.MembersLost.Count > 0 ? diff.MembersLost : diff.MembersUnbound;
        int shown = 0;
        foreach (var m in show)
        {
            if (shown >= MaxDetailLines) break;
            Detail(sb, StatusWord(m.Status) + m.Describe()
                       + (m.Found.Length == 0 ? "" : "   now: " + Trim(m.Found))
                       + (fixable.Contains(m.Key) ? "   [fixable by rewrite]" : ""));
            shown++;
        }
        if (show.Count > shown) Detail(sb, "... " + (show.Count - shown) + " more");
    }

    private static string MembersUnchangedLine(ResilienceDiff diff)
    {
        if (diff.NothingChecked) return NothingCheckedLine(diff);
        var text = ReferenceCount(diff) + ", " + BindVerdict(diff);
        if (diff.MembersNotChecked > 0) text += ", " + diff.MembersNotChecked + " not checked";
        return text;
    }

    /// <summary>
    /// "895 references", plus how Core's own reference set moved since the baseline —
    /// "895 references (+5 since baseline)". Before job FUPD8 that growth was invisible: it
    /// vetoed the unchanged verdict from <c>ResilienceDiff.Unchanged</c> and was rendered
    /// nowhere, so a Core deploy that touched a new game type printed "game build CHANGED" over
    /// a block whose every row said unchanged (R55 finding 5, observed on the test server
    /// 2026-09-05). It is now a detail on this row and nothing else.
    /// </summary>
    private static string ReferenceCount(ResilienceDiff diff)
    {
        var text = diff.MemberCount.ToString(CultureInfo.InvariantCulture) + " references";
        var moved = new List<string>();
        if (diff.MembersNew.Count > 0) moved.Add("+" + diff.MembersNew.Count);
        if (diff.MembersDropped.Count > 0) moved.Add("-" + diff.MembersDropped.Count);
        if (moved.Count > 0) text += " (" + string.Join(", ", moved) + " since baseline)";
        return text;
    }

    /// <summary>
    /// "all bind" is a claim about references that were actually resolved, so it names the
    /// checked count whenever some rows were skipped. Zero checked never reaches here — it gets
    /// <see cref="NothingCheckedLine"/> instead (R31 finding 11).
    /// </summary>
    private static string BindVerdict(ResilienceDiff diff) =>
        diff.MembersNotChecked > 0
            ? diff.MembersChecked.ToString(CultureInfo.InvariantCulture) + " checked, all bind"
            : "all bind";

    private static string NothingCheckedLine(ResilienceDiff diff) =>
        diff.MemberCount.ToString(CultureInfo.InvariantCulture)
        + " references, NONE checked - the interop assemblies could not be read";

    private static string TableLine(BuildFingerprint current)
    {
        int total = current.DetourSitesInTable.Count + current.DetourSitesMissing.Count;
        return "table " + current.AddressTableFileName + " present, "
               + current.DetourSitesInTable.Count + "/" + total + " detour sites have an entry";
    }

    private static string Baseline(ResilienceDiff diff)
    {
        var file = diff.BaselineFile.Length == 0 ? "(stored fingerprint)" : diff.BaselineFile;
        return diff.BaselineCapturedUtc.Length == 0
            ? file
            : file + " (captured " + diff.BaselineCapturedUtc + ")";
    }

    /// <summary>Fixed-width status column so the member lines read as a table.</summary>
    private static string StatusWord(BindingStatus status) => (status switch
    {
        BindingStatus.TypeMissing => "type gone",
        BindingStatus.MemberMissing => "member gone",
        BindingStatus.SignatureChanged => "signature",
        BindingStatus.NotChecked => "unchecked",
        _ => "bound",
    }).PadRight(12);

    private static IEnumerable<string> Take(IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count && i < MaxDetailLines; i++) yield return values[i];
    }

    private static string Trim(string text) =>
        text.Length <= 160 ? text : text.Substring(0, 157) + "...";

    private static void Line(StringBuilder sb, string label, string text) =>
        sb.Append("  ").Append(label.PadRight(11)).Append(' ').Append(text).Append('\n');

    private static void Detail(StringBuilder sb, string text) =>
        sb.Append("              ").Append(text).Append('\n');
}
