using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Progenitor.Native;
using Progenitor.Resilience;
using Xunit;
using ResilienceModule = Progenitor.Resilience.Resilience;

namespace Progenitor.Tests;

/// <summary>
/// Job FUPD (roadmap M4 item 1). The diff logic and the report block, driven by fabricated
/// fingerprints and fabricated member lists. No game, no V Rising install, no metadata files:
/// <see cref="ResilienceDiff"/> takes two objects, and everything the startup block says comes
/// out of it. <see cref="MetadataScan"/>'s own reader is exercised against this test assembly,
/// which is the only real PE the suite is allowed to assume exists.
/// </summary>
public class ResilienceTests
{
    private const string ClientBuild = "247473480-6a147771-ec12531-66fe4b61f7a0d186";
    private const string NextBuild = "248100992-6a1b2c33-ff01234-aabbccdd11223344";

    private static AssemblyFingerprint Asm(string name, long length = 1000, string sha = "aa", string version = "1.0.0.0", string modified = "t0") =>
        new(name, version, length, sha, modified);

    private static MemberBinding Bound(string type, string name, string sig = "System.Void()") =>
        new("ProjectM", type, name, MemberKind.Method, sig, BindingStatus.Bound);

    private static MemberBinding Broken(string type, string name, BindingStatus status, string sig = "System.Void()", string found = "") =>
        new("ProjectM", type, name, MemberKind.Method, sig, status, found);

    private static BuildFingerprint Fingerprint(
        string build = ClientBuild,
        string captured = "2026-09-04T09:00:00Z",
        IReadOnlyList<AssemblyFingerprint>? interop = null,
        bool tablePresent = true,
        IReadOnlyList<string>? inTable = null,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<MemberBinding>? members = null,
        string gameSha = "ffffffff",
        string coreVersion = "0.1.0.0",
        string side = "client",
        string coreMvid = "") =>
        new(
            side,
            build,
            captured,
            coreVersion,
            @"C:\VRising\GameAssembly.dll",
            247473480,
            gameSha,
            @"C:\VRising\BepInEx\interop",
            interop ?? new[] { Asm("ProjectM.dll"), Asm("Unity.Entities.dll") },
            tablePresent,
            inTable ?? new[] { AddressId.SiteReceiveClient, AddressId.SiteReceiveServer, AddressId.SiteKeyProbeFindEntry },
            missing ?? Array.Empty<string>(),
            members ?? new[] { Bound("ProjectM.ClientBootstrapSystem", "OnReliableEventsReceived") },
            "",
            "",
            "",
            BuildFingerprint.CurrentVersion,
            coreMvid);

    // ---------------------------------------------------------------- unchanged

    [Fact]
    public void IdenticalFingerprintsAreUnchanged()
    {
        var diff = ResilienceDiff.Compare(Fingerprint(), Fingerprint(captured: "2026-09-04T10:00:00Z"));

        Assert.True(diff.HasBaseline);
        Assert.True(diff.Unchanged);
        Assert.False(diff.BuildChanged);
        Assert.False(diff.HasBreakage);
        Assert.Empty(diff.MembersLost);
        Assert.Empty(diff.ChangedInterop);
    }

    [Fact]
    public void UnchangedReportIsTheOneLineBlock()
    {
        var text = ResilienceReport.Format(
            ResilienceDiff.Compare(Fingerprint(), Fingerprint(), "build-client-" + ClientBuild + ".json"),
            Fingerprint());

        Assert.StartsWith(ResilienceReport.UnchangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("client-" + ClientBuild, text, StringComparison.Ordinal);
        Assert.Contains("2 assemblies, unchanged", text, StringComparison.Ordinal);
        Assert.Contains("3/3 detour sites have an entry", text, StringComparison.Ordinal);
        Assert.Contains("all bind", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunHasNoBaselineAndIsNotCalledUnchanged()
    {
        var diff = ResilienceDiff.Compare(null, Fingerprint());

        Assert.False(diff.HasBaseline);
        Assert.False(diff.Unchanged);
        Assert.Empty(diff.MembersLost);
        Assert.Contains("first run", ResilienceReport.Format(diff, Fingerprint()), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- interop set

    [Fact]
    public void AddedRemovedAndChangedInteropAssembliesAreReported()
    {
        var before = Fingerprint(interop: new[]
        {
            Asm("ProjectM.dll", 100, "aa"),
            Asm("Gone.dll", 10, "bb"),
        });
        var after = Fingerprint(interop: new[]
        {
            Asm("ProjectM.dll", 200, "cc"),
            Asm("New.dll", 30, "dd"),
        });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.False(diff.Unchanged);
        Assert.Equal("New.dll", Assert.Single(diff.AddedInterop).Name);
        Assert.Equal("Gone.dll", Assert.Single(diff.RemovedInterop).Name);
        var changed = Assert.Single(diff.ChangedInterop);
        Assert.Equal("ProjectM.dll", changed.Name);
        Assert.Contains("size 100 -> 200", changed.Description, StringComparison.Ordinal);
        Assert.Contains("sha aa -> cc", changed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionBumpWithTheSameBytesStillCounts()
    {
        var before = Fingerprint(interop: new[] { Asm("ProjectM.dll", version: "1.0.0.0") });
        var after = Fingerprint(interop: new[] { Asm("ProjectM.dll", version: "1.1.0.0") });

        var changed = Assert.Single(ResilienceDiff.Compare(before, after).ChangedInterop);
        Assert.Contains("version 1.0.0.0 -> 1.1.0.0", changed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingHashOnOneSideDoesNotInventAChange()
    {
        // The startup fast path stores "" for files it did not re-hash. That must not read as drift.
        var before = Fingerprint(interop: new[] { Asm("ProjectM.dll", 100, "aa") });
        var after = Fingerprint(interop: new[] { Asm("ProjectM.dll", 100, "") });

        Assert.Empty(ResilienceDiff.Compare(before, after).ChangedInterop);
    }

    // ---------------------------------------------------------------- address table

    [Fact]
    public void NoTableForTheNewBuildIsBreakageAndNamesTheRebuild()
    {
        var before = Fingerprint();
        var after = Fingerprint(
            build: NextBuild,
            tablePresent: false,
            inTable: Array.Empty<string>(),
            missing: new[] { AddressId.SiteKeyProbeFindEntry, AddressId.SiteReceiveClient, AddressId.SiteReceiveServer });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.True(diff.BuildChanged);
        Assert.True(diff.AddressTableMissing);
        Assert.True(diff.HasBreakage);
        Assert.Equal(3, diff.DetourSitesLost.Count);

        var text = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("was client-" + ClientBuild, text, StringComparison.Ordinal);
        Assert.Contains("NO TABLE for this build (client-" + NextBuild + ".json)", text, StringComparison.Ordinal);
        Assert.Contains(AddressId.FormatRebuildCommand("client"), text, StringComparison.Ordinal);
        Assert.Contains(ResilienceReport.Footer, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleDetourSiteLosingItsEntryIsNamed()
    {
        var before = Fingerprint();
        var after = Fingerprint(
            build: NextBuild,
            inTable: new[] { AddressId.SiteReceiveClient, AddressId.SiteReceiveServer },
            missing: new[] { AddressId.SiteKeyProbeFindEntry });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Equal(new[] { AddressId.SiteKeyProbeFindEntry }, diff.DetourSitesLost);
        Assert.True(diff.HasBreakage);
        Assert.False(diff.AddressTableMissing);

        var text = ResilienceReport.Format(diff, after);
        Assert.Contains("2/3 detour sites have an entry", text, StringComparison.Ordinal);
        Assert.Contains("lost entry " + AddressId.SiteKeyProbeFindEntry, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------- the drift-map join (job FCOMPAT3)

    /// <summary>
    /// The join is over the two models and nothing else: a <see cref="ResilienceDiff"/> and a
    /// <see cref="Progenitor.Compat.DriftMap"/>. No install, no game, no map file on disk — the
    /// keys are the same <c>Assembly|Type|Name|Signature</c> string on both sides, which is the
    /// whole point of having one identity scheme.
    /// </summary>
    private static Progenitor.Compat.DriftMap MapFixing(params Progenitor.Compat.DriftRule[] rules) =>
        Progenitor.Compat.DriftMap.Create("client", ClientBuild, rules);

    private static Progenitor.Compat.DriftRule Rename(string type, string from, string to, string sig = "System.Void()") =>
        new(
            Progenitor.Compat.DriftRule.Key("ProjectM", type, from, sig),
            Progenitor.Compat.DriftRule.Key("ProjectM", type, to, sig),
            Progenitor.Compat.DriftKind.MemberRename,
            Progenitor.Compat.DriftConfidence.High,
            Progenitor.Compat.DriftSource.Generated);

    [Fact]
    public void TheMembersBlockSaysHowMuchOfTheBreakageTheRewriterCanAbsorb()
    {
        var before = Fingerprint(members: new[]
        {
            Bound("ProjectM.Sys", "Renamed"),
            Bound("ProjectM.Sys", "Deleted"),
        });
        var after = Fingerprint(members: new[]
        {
            Broken("ProjectM.Sys", "Renamed", BindingStatus.MemberMissing),
            Broken("ProjectM.Sys", "Deleted", BindingStatus.MemberMissing),
        });

        var diff = ResilienceDiff.Compare(before, after)
            .JoinDriftMap(MapFixing(Rename("ProjectM.Sys", "Renamed", "RenamedNow")));

        Assert.True(diff.HasDriftMap);
        Assert.Equal("Renamed", Assert.Single(diff.MembersFixableByRewrite).Name);
        Assert.Equal("Deleted", Assert.Single(diff.MembersNeedingAuthor).Name);

        string text = ResilienceReport.Format(diff, after);
        Assert.Contains("2 do not bind, 1 fixable by the drift map, 1 need the plugin author",
            text, StringComparison.Ordinal);
        // The map that produced the claim is named on its own row, hash and all.
        Assert.Contains("rewrite     " + Progenitor.Compat.DriftMapStore.FileName(ClientBuild),
            text, StringComparison.Ordinal);
        // And the fixable row is marked in the detail list, so the two halves are readable.
        Assert.Contains("ProjectM!ProjectM.Sys.Renamed System.Void()   [fixable by rewrite]",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectM.Sys.Deleted System.Void()   [fixable by rewrite]",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberFollowsItsDeclaringTypeWhenOnlyTheTypeMoved()
    {
        // A typeMove/typeRename rule carries every member of that type with it, so a member row
        // with no rule of its own still joins through the type key.
        var after = Fingerprint(members: new[] { Broken("ProjectM.Mover", "Step", BindingStatus.TypeMissing) });
        var diff = ResilienceDiff.Compare(null, after).JoinDriftMap(MapFixing(
            new Progenitor.Compat.DriftRule(
                Progenitor.Compat.DriftRule.TypeKey("ProjectM", "ProjectM.Mover"),
                Progenitor.Compat.DriftRule.TypeKey("ProjectM.Shared", "ProjectM.Mover"),
                Progenitor.Compat.DriftKind.TypeMove,
                Progenitor.Compat.DriftConfidence.Exact,
                Progenitor.Compat.DriftSource.Generated)));

        Assert.Single(diff.MembersFixableByRewrite);
        Assert.Empty(diff.MembersNeedingAuthor);
    }

    [Fact]
    public void AGenericInstantiationRowJoinsTheRuleTheRewriterWillActuallyFind()
    {
        // R34 finding 10: this is the join the finding is about. The scan reduces a TypeSpec
        // MemberRef parent to the open type (NativeArray`1) and so its row is keyed that way,
        // while the rewriter reads the operand's declaring type with Cecil and got the
        // instantiated form back. The map's dictionary is ordinal, so "fixable by rewrite" was
        // decided on a key the rewriter would never look up: the operator vetted the map, set
        // Apply, restarted, and the plugin crashed with no Rewrite: line naming those rules.
        //
        // Both renderings now go through DriftRule.OpenTypeText, so the row joins whichever form
        // the map's author wrote — and it is the same string the rewriter looks up.
        const string open = "Unity.Collections.NativeArray`1";
        const string instantiated = "Unity.Collections.NativeArray`1<ProjectM.Entity>";
        var after = Fingerprint(members: new[]
        {
            new MemberBinding("Unity.Collections", open, "Length", MemberKind.Method,
                "System.Int32()", BindingStatus.MemberMissing),
        });

        var diff = ResilienceDiff.Compare(null, after).JoinDriftMap(MapFixing(
            new Progenitor.Compat.DriftRule(
                Progenitor.Compat.DriftRule.Key("Unity.Collections", instantiated, "Length", "System.Int32()"),
                Progenitor.Compat.DriftRule.Key("Unity.Collections", open, "Count", "System.Int32()"),
                Progenitor.Compat.DriftKind.MemberRename,
                Progenitor.Compat.DriftConfidence.High,
                Progenitor.Compat.DriftSource.Hand)));

        Assert.Equal("Length", Assert.Single(diff.MembersFixableByRewrite).Name);
        Assert.Empty(diff.MembersNeedingAuthor);
        Assert.Contains("[fixable by rewrite]", ResilienceReport.Format(diff, after), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeuristicRuleIsNeverCountedAsAFix()
    {
        // Heuristic and addParam rules are reported and never applied, so promising them as a
        // fix would be a claim the report cannot cash.
        var after = Fingerprint(members: new[] { Broken("ProjectM.Sys", "Gone", BindingStatus.MemberMissing) });
        var diff = ResilienceDiff.Compare(null, after).JoinDriftMap(MapFixing(
            new Progenitor.Compat.DriftRule(
                Progenitor.Compat.DriftRule.Key("ProjectM", "ProjectM.Sys", "Gone", "System.Void()"),
                Progenitor.Compat.DriftRule.Key("ProjectM", "ProjectM.Sys", "Guessed", "System.Void()"),
                Progenitor.Compat.DriftKind.MemberRename,
                Progenitor.Compat.DriftConfidence.Heuristic,
                Progenitor.Compat.DriftSource.Generated)));

        Assert.Empty(diff.MembersFixableByRewrite);
        Assert.Single(diff.MembersNeedingAuthor);
        Assert.Contains("1 do not bind, 0 fixable by the drift map, 1 need the plugin author",
            ResilienceReport.Format(diff, after), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoDriftMapTheMembersBlockIsExactlyWhatItAlwaysWas()
    {
        var after = Fingerprint(members: new[] { Broken("ProjectM.Sys", "Gone", BindingStatus.MemberMissing) });
        var diff = ResilienceDiff.Compare(null, after).JoinDriftMap(null);

        Assert.False(diff.HasDriftMap);
        Assert.Empty(diff.MembersFixableByRewrite);
        Assert.Single(diff.MembersNeedingAuthor);
        string text = ResilienceReport.Format(diff, after);
        Assert.Contains("1 do not bind", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixable by the drift map", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rewrite     ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJoinIsWiredThroughASeamThatDefaultsToNoMap()
    {
        // Compare consults ResilienceDiff.DriftMapProvider, which Core's Plugin.Install points at
        // the running build's map and which is null everywhere else (tests, the offline tool).
        var after = Fingerprint(members: new[] { Broken("ProjectM.Sys", "Renamed", BindingStatus.MemberMissing) });
        Assert.Null(ResilienceDiff.DriftMapProvider);
        Assert.False(ResilienceDiff.Compare(null, after).HasDriftMap);

        try
        {
            ResilienceDiff.DriftMapProvider = () => MapFixing(Rename("ProjectM.Sys", "Renamed", "RenamedNow"));
            Assert.Single(ResilienceDiff.Compare(null, after).MembersFixableByRewrite);

            // A provider that throws is "no map", never a failed report.
            ResilienceDiff.DriftMapProvider = () => throw new InvalidOperationException("boom");
            Assert.False(ResilienceDiff.Compare(null, after).HasDriftMap);
        }
        finally { ResilienceDiff.DriftMapProvider = null; }
    }

    // ---------------------------------------------------------------- members

    [Fact]
    public void AMemberThatVanishesIsReportedAsLost()
    {
        var kept = Bound("ProjectM.Kept", "Stays");
        var before = Fingerprint(members: new[] { kept, Bound("ProjectM.Doomed", "Gone") });
        var after = Fingerprint(members: new[]
        {
            kept,
            Broken("ProjectM.Doomed", "Gone", BindingStatus.MemberMissing),
        });

        var diff = ResilienceDiff.Compare(before, after);

        var lost = Assert.Single(diff.MembersLost);
        Assert.Equal("Gone", lost.Name);
        Assert.Equal(BindingStatus.MemberMissing, lost.Status);
        Assert.Single(diff.MembersUnbound);
        Assert.True(diff.HasBreakage);
        Assert.False(diff.Unchanged);

        var text = ResilienceReport.Format(diff, after);
        Assert.Contains("1 do not bind", text, StringComparison.Ordinal);
        Assert.Contains("member gone ProjectM!ProjectM.Doomed.Gone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignatureChangeShowsWhatTheBuildNowOffers()
    {
        var before = Fingerprint(members: new[] { Bound("ProjectM.Sys", "Update", "System.Void()") });
        var after = Fingerprint(members: new[]
        {
            Broken("ProjectM.Sys", "Update", BindingStatus.SignatureChanged, "System.Void()", "System.Void(System.Single)"),
        });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Equal(BindingStatus.SignatureChanged, Assert.Single(diff.MembersLost).Status);
        var text = ResilienceReport.Format(diff, after);
        Assert.Contains("signature   ProjectM!ProjectM.Sys.Update System.Void()", text, StringComparison.Ordinal);
        Assert.Contains("now: System.Void(System.Single)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeThatMovedAssembliesReadsAsTypeMissing()
    {
        var before = Fingerprint(members: new[] { new MemberBinding("ProjectM", "ProjectM.Foo", "", MemberKind.Type, "", BindingStatus.Bound) });
        var after = Fingerprint(members: new[] { new MemberBinding("ProjectM", "ProjectM.Foo", "", MemberKind.Type, "", BindingStatus.TypeMissing) });

        var diff = ResilienceDiff.Compare(before, after);
        Assert.Equal(BindingStatus.TypeMissing, Assert.Single(diff.MembersLost).Status);
        Assert.Contains("type gone   ProjectM!ProjectM.Foo", ResilienceReport.Format(diff, after), StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildingCoreShowsAsNewAndDroppedReferencesNotAsBreakage()
    {
        var before = Fingerprint(members: new[] { Bound("ProjectM.Old", "Call") }, coreVersion: "0.1.0.0");
        var after = Fingerprint(members: new[] { Bound("ProjectM.New", "Call") }, coreVersion: "0.2.0.0");

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Equal("Call", Assert.Single(diff.MembersNew).Name);
        Assert.Equal("ProjectM.Old", Assert.Single(diff.MembersDropped).Type);
        Assert.Empty(diff.MembersLost);
        Assert.True(diff.CoreChanged);
        Assert.False(diff.HasBreakage);
        // The game did not move, so the verdict does not either (job FUPD8); the block still
        // names Core and now says which way its reference set went.
        Assert.False(diff.GameChanged);
        Assert.True(diff.Unchanged);
        var report = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.UnchangedHeadline, report, StringComparison.Ordinal);
        Assert.Contains("core        Progenitor 0.1.0.0 -> 0.2.0.0", report, StringComparison.Ordinal);
        Assert.Contains("1 references (+1, -1 since baseline), all bind", report, StringComparison.Ordinal);
    }

    // ------------------------------------------- Core's own reference set (job FUPD8, R55 #5)

    /// <summary>
    /// R55 finding 5, measured on the test server on 2026-09-05
    /// (<c>_testserver\server\BepInEx\LogOutput.log:16385</c>): the block said
    /// <c>game build CHANGED</c> while every row in it — build key, 169 interop assemblies, the
    /// address table, 3/3 detour sites — said unchanged, because Core's own reference set had
    /// grown from 890 to 895 in that deploy. <c>MembersNew</c> vetoed the verdict and was
    /// rendered nowhere. The verdict is about the game; Core-side growth is a row detail.
    /// </summary>
    [Fact]
    public void CoreGrowingItsOwnReferenceSetIsARowDetailNotAChangedGameBuild()
    {
        var baselineMembers = new List<MemberBinding>();
        for (int i = 0; i < 890; i++) baselineMembers.Add(Bound("ProjectM.Type" + i, "Call"));
        var currentMembers = new List<MemberBinding>(baselineMembers);
        for (int i = 0; i < 5; i++) currentMembers.Add(Bound("ProjectM.Added" + i, "Call"));

        var before = Fingerprint(members: baselineMembers);
        var after = Fingerprint(members: currentMembers);

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Equal(5, diff.MembersNew.Count);
        Assert.Empty(diff.MembersDropped);
        Assert.Empty(diff.MembersUnbound);
        Assert.False(diff.GameChanged);
        Assert.True(diff.Unchanged);
        Assert.False(diff.HasBreakage);

        var text = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.UnchangedHeadline, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("895 references (+5 since baseline), all bind", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewReferenceThatDoesNotBindIsBreakageAndNotAChangedGameBuild()
    {
        var before = Fingerprint(members: new[] { Bound("ProjectM.Sys", "Update") });
        var after = Fingerprint(members: new[]
        {
            Bound("ProjectM.Sys", "Update"),
            Broken("ProjectM.Fresh", "Missing", BindingStatus.MemberMissing),
        });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Single(diff.MembersNew);
        Assert.Empty(diff.MembersLost);          // it was never in the baseline to lose
        Assert.Single(diff.MembersUnbound);
        Assert.True(diff.HasBreakage);
        Assert.False(diff.Unchanged);
        Assert.False(diff.GameChanged);          // nothing about the game moved

        var text = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.ReferencesBreakageHeadline, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("2 references (+1 since baseline), 1 do not bind", text, StringComparison.Ordinal);
        Assert.Contains("member gone ProjectM!ProjectM.Fresh.Missing", text, StringComparison.Ordinal);
        Assert.Contains(ResilienceReport.Footer, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealGameChangeStillGetsTheChangedHeadlineEvenWhenCoreAlsoGrew()
    {
        var before = Fingerprint(members: new[] { Bound("ProjectM.Sys", "Update") });
        var after = Fingerprint(
            build: NextBuild,
            members: new[] { Bound("ProjectM.Sys", "Update"), Bound("ProjectM.Fresh", "Call") });

        var diff = ResilienceDiff.Compare(before, after);

        Assert.True(diff.GameChanged);
        Assert.False(diff.Unchanged);
        var text = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("2 references (+1 since baseline), all bind", text, StringComparison.Ordinal);
    }

    // ------------------------------ one predicate for breakage (job FUPD9, R56 findings 4 and 9)

    /// <summary>
    /// Every term a verdict reads, paired with the smallest edit that turns it on. The list is
    /// pinned against the type by
    /// <see cref="EveryBooleanTermOfTheDiffIsEitherAVerdictTermOrExplicitlyNotOne"/>, so a term
    /// added to <see cref="ResilienceDiff"/> and forgotten here fails a test rather than
    /// silently shrinking the combination sweep below.
    /// <para>
    /// <see cref="ResilienceDiff.HasBaseline"/> is the odd one: every other entry pushes a
    /// verdict AWAY from <c>Unchanged</c> and this one is required FOR it, which the sweep's
    /// expectation spells out rather than hiding in a count.
    /// </para>
    /// </summary>
    private static readonly (string Name, Action<ResilienceDiff> Turn)[] VerdictTerms =
    {
        ("HasBaseline", d => SetDiff(d, "HasBaseline", true)),
        ("BuildChanged", d => SetDiff(d, "BuildChanged", true)),
        ("GameAssemblyChangedUnderSameBuildId", d => SetDiff(d, "GameAssemblyChangedUnderSameBuildId", true)),
        ("AddedInterop", d => SetDiff(d, "AddedInterop", new[] { Asm("New.dll") })),
        ("RemovedInterop", d => SetDiff(d, "RemovedInterop", new[] { Asm("Gone.dll") })),
        ("ChangedInterop", d => SetDiff(d, "ChangedInterop",
            new[] { new InteropChange(Asm("ProjectM.dll", 100), Asm("ProjectM.dll", 200)) })),
        ("AddressTableMissing", d => SetDiff(d, "AddressTableMissing", true)),
        // NothingChecked is derived: rows exist and not one of them was resolved.
        ("NothingChecked", d => { SetDiff(d, "MemberCount", 1); SetDiff(d, "MembersNotChecked", 1); }),
        ("DetourSitesLost", d => SetDiff(d, "DetourSitesLost", new[] { AddressId.SiteReceiveClient })),
        ("DetourSitesWithoutEntry", d => SetDiff(d, "DetourSitesWithoutEntry", new[] { AddressId.SiteKeyProbeFindEntry })),
        ("MembersLost", d => SetDiff(d, "MembersLost", new[] { Broken("ProjectM.Sys", "Gone", BindingStatus.MemberMissing) })),
        ("MembersUnbound", d => SetDiff(d, "MembersUnbound", new[] { Broken("ProjectM.Sys", "Gone", BindingStatus.MemberMissing) })),
    };

    /// <summary>
    /// Facts the diff stores and no verdict reads: Core's own reference set moved, or Core itself
    /// did. The sweep toggles them exactly as it toggles a verdict term - that is the point - and
    /// expects every verdict to come out the same either way.
    /// </summary>
    /// <remarks>
    /// Job FUPD10, R57 finding 8. These three used to sit in <see cref="VerdictTerms"/>, whose
    /// name says the opposite of what they are, and the sweep pinned the survivors with a literal
    /// <c>1 &lt;&lt; 3</c>. A fourth Core-side fact copying that precedent would have failed with
    /// <c>Assert.Equal(8, 16)</c> - a message naming neither the term nor the reason - and filing
    /// it correctly would have shrunk the sweep silently. Split, both hazards go away and the
    /// number stops being a literal.
    /// </remarks>
    private static readonly (string Name, Action<ResilienceDiff> Turn)[] NonVerdictStoredFacts =
    {
        ("MembersNew", d => SetDiff(d, "MembersNew", new[] { Bound("ProjectM.Fresh", "Call") })),
        ("MembersDropped", d => SetDiff(d, "MembersDropped", new[] { Bound("ProjectM.Old", "Call") })),
        ("CoreChanged", d => SetDiff(d, "CoreChanged", true)),
    };

    /// <summary>Everything the sweep toggles: the verdict terms first, so a mask bit keeps its meaning.</summary>
    private static readonly (string Name, Action<ResilienceDiff> Turn)[] SweptTerms =
        VerdictTerms.Concat(NonVerdictStoredFacts).ToArray();

    /// <summary>The three predicates the sweep is about. Derived, never set.</summary>
    private static readonly string[] Verdicts = { "GameChanged", "Unchanged", "HasBreakage" };

    /// <summary>
    /// Boolean-or-list facts on the diff that deliberately feed no verdict: the drift-map join
    /// (a report detail, never a decision) and a reference that started binding again.
    /// </summary>
    private static readonly string[] NotVerdictTerms =
    {
        "HasDriftMap", "MembersRegained", "MembersFixableByRewrite", "MembersNeedingAuthor",
    };

    private static void SetDiff(ResilienceDiff diff, string property, object value)
    {
        var p = typeof(ResilienceDiff).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("ResilienceDiff has no property " + property);
        var setter = p.GetSetMethod(nonPublic: true)
                     ?? throw new InvalidOperationException("ResilienceDiff." + property + " has no setter");
        setter.Invoke(diff, new[] { value });
    }

    private static ResilienceDiff DiffWith(int mask)
    {
        var diff = (ResilienceDiff)Activator.CreateInstance(typeof(ResilienceDiff), nonPublic: true)!;
        for (int i = 0; i < SweptTerms.Length; i++)
            if ((mask & (1 << i)) != 0) SweptTerms[i].Turn(diff);
        return diff;
    }

    /// <summary>
    /// R56 finding 4. <c>Unchanged</c> and <c>HasBreakage</c> were two hand-written lists of
    /// terms and they disagreed about <c>DetourSitesWithoutEntry</c>: a site missing from a table
    /// that is otherwise present was "unchanged" <b>and</b> breakage at once, so
    /// <c>tools/resilience-report.ps1</c> exited 1 over a one-line block headlined "game build
    /// unchanged".
    /// <para>
    /// What this sweep asserts is <b>not</b> <c>Unchanged =&gt; !HasBreakage</c>, which since job
    /// FUPD9 is a tautology over the definition (<c>Unchanged</c> literally reads
    /// <c>!HasBreakage</c>) and so cannot fail (R57 finding 8). It asserts the thing the
    /// definition does <em>not</em> give you: that <see cref="VerdictTerms"/> and
    /// <see cref="NonVerdictStoredFacts"/> are correctly sorted - every term in the first reaches
    /// a verdict, every fact in the second reaches none - by predicting <c>Unchanged</c> from the
    /// mask alone, over all 2^n combinations. A term that stops feeding a verdict (a predicate
    /// dropping a clause, which is exactly FUPD8's bug) fails here, and so does a Core-side fact
    /// quietly wired into one.
    /// </para>
    /// </summary>
    [Fact]
    public void UnchangedIsExactlyABaselineWithNoVerdictTermOnForEveryCombination()
    {
        int combinations = 1 << SweptTerms.Length;
        int unchanged = 0;
        for (int mask = 0; mask < combinations; mask++)
        {
            var diff = DiffWith(mask);

            // Predicted from the NAMES, not from the predicates: a baseline, and not one of the
            // other verdict terms turned on. Anything in NonVerdictStoredFacts is free to vary.
            bool expected = (mask & 1) != 0;   // HasBaseline is VerdictTerms[0]
            for (int i = 1; i < VerdictTerms.Length; i++)
                if ((mask & (1 << i)) != 0) expected = false;

            Assert.True(
                diff.Unchanged == expected,
                (expected ? "expected Unchanged and got a verdict" : "expected a verdict and got Unchanged")
                + " for terms: " + Terms(mask));

            if (!diff.Unchanged) continue;
            unchanged++;
            Assert.False(diff.HasBreakage, "Unchanged with breakage for terms: " + Terms(mask));
            Assert.False(diff.GameChanged, "Unchanged with a moved game for terms: " + Terms(mask));
            Assert.True(diff.HasBaseline, "Unchanged without a baseline for terms: " + Terms(mask));
        }

        // Falls out of the equality above rather than being a magic number, and the message names
        // the list a new non-verdict fact belongs in.
        Assert.True(
            unchanged == 1 << NonVerdictStoredFacts.Length,
            "the surviving combinations are one per subset of NonVerdictStoredFacts ("
            + string.Join(", ", NonVerdictStoredFacts.Select(t => t.Name)) + "); got " + unchanged);
    }

    /// <summary>
    /// The list above is only worth the sweep if it is the whole list. Every boolean or list
    /// property on the diff is either a term the sweep toggles, one of the three verdicts, or
    /// named as deliberately not feeding a verdict.
    /// </summary>
    [Fact]
    public void EveryBooleanTermOfTheDiffIsEitherAVerdictTermOrExplicitlyNotOne()
    {
        var facts = typeof(ResilienceDiff)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool)
                        || (p.PropertyType.IsGenericType
                            && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var accounted = SweptTerms.Select(t => t.Name)
            .Concat(Verdicts)
            .Concat(NotVerdictTerms)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(accounted, facts);
    }

    private static string Terms(int mask)
    {
        var on = new List<string>();
        for (int i = 0; i < SweptTerms.Length; i++)
            if ((mask & (1 << i)) != 0) on.Add(SweptTerms[i].Name);
        return on.Count == 0 ? "(none)" : string.Join(" + ", on);
    }

    /// <summary>
    /// R56 finding 4, measured: two fingerprints identical in every respect, each carrying one
    /// detour site with a table entry and one without. Before job FUPD9 this printed the
    /// one-line "game build unchanged" block — with the whole story in an <c>addresslib</c> row
    /// reading <c>1/2</c> — while the offline tool exited 1 over it.
    /// </summary>
    [Fact]
    public void ADetourSiteWithNoEntryUnderAnUnchangedBuildGetsTheFullBlock()
    {
        var print = Fingerprint(
            inTable: new[] { AddressId.SiteReceiveClient },
            missing: new[] { AddressId.SiteKeyProbeFindEntry });

        var diff = ResilienceDiff.Compare(print, print);

        Assert.True(diff.HasBreakage);
        Assert.False(diff.GameChanged);       // nothing about the game moved
        Assert.False(diff.Unchanged);         // ... and the two now agree

        var text = ResilienceReport.Format(diff, print);
        Assert.StartsWith(ResilienceReport.DetourSitesBreakageHeadline, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceReport.UnchangedHeadline, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceReport.ChangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("1/2 detour sites have an entry", text, StringComparison.Ordinal);
        Assert.Contains("no entry  " + AddressId.SiteKeyProbeFindEntry, text, StringComparison.Ordinal);
        Assert.Contains(AddressId.FormatRebuildCommand("client"), text, StringComparison.Ordinal);
        Assert.Contains(ResilienceReport.Footer, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R56 finding 9, observed at <c>_testserver\server\BepInEx\LogOutput.log:17587</c>: the
    /// members row said <c>897 references (+2 since baseline)</c> and the <c>core</c> row that
    /// job FUPD8 added to say why was withheld, because it keyed on the assembly version and a
    /// developer-tree deploy does not bump it. Job FUPD9 fired the row on the reference set
    /// moving; this is the half R57 finding 8 said was still missing - the row now names which
    /// Core, because the fingerprint records the MVID the compiler stamps into every build and
    /// that moves when the version does not.
    /// </summary>
    [Fact]
    public void AMovedReferenceSetNamesTheCoreItMovedUnderEvenWhenTheVersionDidNot()
    {
        var before = Fingerprint(
            members: new[] { Bound("ProjectM.Sys", "Update") },
            coreVersion: "0.1.0.0", coreMvid: "a1b2c3d4e5f60718293a4b5c6d7e8f90");
        var after = Fingerprint(
            members: new[] { Bound("ProjectM.Sys", "Update"), Bound("ProjectM.Fresh", "Call") },
            coreVersion: "0.1.0.0", coreMvid: "0f1e2d3c4b5a69788796a5b4c3d2e1f0");

        var diff = ResilienceDiff.Compare(before, after);

        Assert.Single(diff.MembersNew);
        // The version did not move and the deploy is still a different Core, which is exactly
        // what the version alone could never say.
        Assert.True(diff.CoreChanged);
        Assert.True(diff.Unchanged);          // a Core deploy is not a game move (job FUPD8)

        var text = ResilienceReport.Format(diff, after);
        Assert.StartsWith(ResilienceReport.UnchangedHeadline, text, StringComparison.Ordinal);
        Assert.Contains("2 references (+1 since baseline), all bind", text, StringComparison.Ordinal);
        Assert.Contains("core        Progenitor 0.1.0.0/a1b2c3d4 -> 0.1.0.0/0f1e2d3c",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row FUPD9 printed on every one of these deploys, kept for the one case that can still
    /// reach it: a baseline stored before the MVID field existed, or a run whose Core was loaded
    /// from memory with no folder to read it from. There is no identity to move, so the row says
    /// the count instead of pretending to answer "which Core".
    /// </summary>
    [Fact]
    public void AMovedReferenceSetWithNoRecordedMvidSaysSoAndNamesTheCount()
    {
        var before = Fingerprint(members: new[] { Bound("ProjectM.Sys", "Update") }, coreVersion: "0.1.0.0");
        var after = Fingerprint(
            members: new[] { Bound("ProjectM.Fresh", "Call"), Bound("ProjectM.Other", "Call") },
            coreVersion: "0.1.0.0");

        var diff = ResilienceDiff.Compare(before, after);

        Assert.False(diff.CoreChanged);
        var text = ResilienceReport.Format(diff, after);
        Assert.Contains("core        Core references moved under the same 0.1.0.0 (+2/-1)",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A baseline from before job FUPD10 records no MVID at all. "" is "cannot say", never
    /// "changed": a stored fingerprint one deploy old must not start reporting a moved Core on
    /// every run just because the field is new.
    /// </summary>
    [Fact]
    public void AnAbsentMvidOnEitherSideIsNotEvidenceThatCoreMoved()
    {
        var beforeFieldExisted = Fingerprint(coreVersion: "0.1.0.0");
        var withMvid = Fingerprint(coreVersion: "0.1.0.0", coreMvid: "a1b2c3d4e5f60718293a4b5c6d7e8f90");

        Assert.False(ResilienceDiff.Compare(beforeFieldExisted, withMvid).CoreChanged);
        Assert.False(ResilienceDiff.Compare(withMvid, beforeFieldExisted).CoreChanged);
        Assert.True(ResilienceDiff.Compare(beforeFieldExisted, beforeFieldExisted).Unchanged);
    }

    /// <summary>
    /// The <c>core</c> row's two halves come from one assembly (job FUPD11, R58 finding 9).
    /// <see cref="ResilienceOptions.CoreVersion"/> and <see cref="ResilienceOptions.CoreMvid"/>
    /// are both filled in <c>Resilience.DescribeEnvironment</c> from that one assembly, so
    /// <c>0.1.0.0/a1b2c3d4</c> names one Core. FUPD10 read the MVID off <c>Progenitor.dll</c> on
    /// disk while the version came from the running image: two sources, one identity, and in the
    /// game the two are routinely different files — Bootstrap loads Core from bytes, so the path
    /// is only ever a folder fallback that a deploy may have staged over.
    /// </summary>
    [Fact]
    public void TheCoreVersionAndTheCoreMvidAreBothReadOffTheRunningAssembly()
    {
        var running = typeof(ResilienceModule).Assembly;
        var options = ResilienceModule.DescribeEnvironment(out _);

        Assert.Equal(running.GetName().Version?.ToString() ?? "", options.CoreVersion);
        Assert.Equal(running.ManifestModule.ModuleVersionId.ToString("N"), options.CoreMvid);
        Assert.Equal(32, options.CoreMvid.Length);
    }

    /// <summary>
    /// And the identity survives the run it was added for. An unresolvable core folder leaves
    /// <see cref="ResilienceOptions.CoreAssemblyPath"/> empty and switches the member scan off;
    /// under FUPD10 that emptied the MVID too, so the row degraded to naming a version alone on
    /// precisely the run that most needs to say which Core is running.
    /// </summary>
    [Fact]
    public void TheCoreMvidSurvivesAnUnresolvableCoreFolder()
    {
        var path = TempBinary(4096);
        try
        {
            var options = GameOnlyOptions(path, "2222");
            options.CoreVersion = "0.1.0.0";
            options.CoreMvid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
            // The FUPD5 failure shape: no folder, no file, no member scan.
            Assert.Equal("", options.CoreAssemblyPath);
            Assert.False(options.ScanMembers);

            var print = ResilienceCheck.Capture(options, null, new List<string>());

            Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", print.CoreMvid);
            Assert.Equal("0.1.0.0/a1b2c3d4", print.CoreIdentity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The MVID survives the store round trip, or the baseline can never carry one.</summary>
    [Fact]
    public void TheCoreMvidRoundTripsThroughTheStoredFingerprint()
    {
        var print = Fingerprint(coreVersion: "0.1.0.0", coreMvid: "a1b2c3d4e5f60718293a4b5c6d7e8f90");
        Assert.True(BuildFingerprint.TryParse(print.ToJson(), out var read, out var error), error);
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", read!.CoreMvid);
        Assert.Equal("0.1.0.0/a1b2c3d4", read.CoreIdentity);

        // And a stored file written before the field existed reads back as "cannot say".
        var older = print.ToJson().Replace("\"coreMvid\": \"a1b2c3d4e5f60718293a4b5c6d7e8f90\",", "");
        Assert.True(BuildFingerprint.TryParse(older, out var legacy, out error), error);
        Assert.Equal("", legacy!.CoreMvid);
        Assert.Equal("0.1.0.0", legacy.CoreIdentity);
    }

    /// <summary>A steady reference set says nothing about Core at all: no row.</summary>
    [Fact]
    public void AnUnmovedReferenceSetPrintsNoCoreRow()
    {
        var text = ResilienceReport.Format(ResilienceDiff.Compare(Fingerprint(), Fingerprint()), Fingerprint());

        Assert.StartsWith(ResilienceReport.UnchangedHeadline, text, StringComparison.Ordinal);
        Assert.DoesNotContain("core ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceThatStartsBindingAgainIsRegained()
    {
        var before = Fingerprint(members: new[] { Broken("ProjectM.Sys", "Update", BindingStatus.MemberMissing) });
        var after = Fingerprint(members: new[] { Bound("ProjectM.Sys", "Update") });

        var diff = ResilienceDiff.Compare(before, after);
        Assert.Single(diff.MembersRegained);
        Assert.Empty(diff.MembersLost);
        Assert.False(diff.HasBreakage);
    }

    [Fact]
    public void NotCheckedRowsAreCountedButNeverCalledBreakage()
    {
        var rows = new[]
        {
            Bound("ProjectM.Sys", "Update"),
            Broken("ProjectM.Sys", "Generic", BindingStatus.NotChecked),
        };
        var diff = ResilienceDiff.Compare(Fingerprint(members: rows), Fingerprint(members: rows));

        Assert.Equal(1, diff.MembersNotChecked);
        Assert.Empty(diff.MembersUnbound);
        Assert.False(diff.HasBreakage);
        Assert.True(diff.Unchanged);
        Assert.Contains("1 not checked", ResilienceReport.Format(diff, Fingerprint(members: rows)), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- build id edge case

    [Fact]
    public void SameBuildIdWithDifferentGameAssemblyBytesIsCalledOut()
    {
        // The build id hashes only the first 64 KiB. A patch that leaves the header alone
        // must not read as "unchanged" once the full hash disagrees.
        var diff = ResilienceDiff.Compare(Fingerprint(gameSha: "1111"), Fingerprint(gameSha: "2222"));

        Assert.False(diff.BuildChanged);
        Assert.True(diff.GameAssemblyChangedUnderSameBuildId);
        Assert.False(diff.Unchanged);
        Assert.Contains("SAME build id", ResilienceReport.Format(diff, Fingerprint(gameSha: "2222")), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void FingerprintRoundTripsThroughJson()
    {
        var original = Fingerprint(
            interop: new[] { Asm("ProjectM.dll", 12873216, "4f2a", "1.0.0.0", "2026-08-29T06:09:00.0000000Z") },
            missing: new[] { AddressId.SiteKeyProbeFindEntry },
            inTable: new[] { AddressId.SiteReceiveClient },
            members: new[] { Broken("ProjectM.Sys", "Update", BindingStatus.SignatureChanged, "System.Void()", "System.Void(System.Single)") });

        Assert.True(BuildFingerprint.TryParse(original.ToJson(), out var parsed, out var error));
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(original.Key, parsed!.Key);
        Assert.Equal(original.CapturedUtc, parsed.CapturedUtc);
        Assert.Equal(original.GameAssemblySha256, parsed.GameAssemblySha256);
        Assert.Equal("1.0.0.0", Assert.Single(parsed.Interop).Version);
        Assert.Equal(BindingStatus.SignatureChanged, Assert.Single(parsed.Members).Status);
        Assert.Equal("System.Void(System.Single)", parsed.Members[0].Found);
        Assert.Equal(original.DetourSitesMissing, parsed.DetourSitesMissing);

        // Nothing drifted across the round trip: no member appeared, vanished or lost a binding,
        // and the game side is identical. The one row was already broken in the baseline, so the
        // diff is breakage rather than the "unchanged" one-liner — which is the point of job
        // FUPD8's second clause: that compact block prints "all bind", and it must not be reached
        // while a reference does not (before FUPD8 this exact fingerprint reached it).
        var roundTrip = ResilienceDiff.Compare(original, parsed);
        Assert.False(roundTrip.GameChanged);
        Assert.Empty(roundTrip.MembersNew);
        Assert.Empty(roundTrip.MembersDropped);
        Assert.Empty(roundTrip.MembersLost);
        Assert.True(roundTrip.HasBreakage);
        Assert.False(roundTrip.Unchanged);
        Assert.DoesNotContain("all bind", ResilienceReport.Format(roundTrip, parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedStoreIsAMissingBaselineNotAnException()
    {
        Assert.False(BuildFingerprint.TryParse("{\"side\": \"clien", out var parsed, out var error));
        Assert.Null(parsed);
        Assert.NotNull(error);

        Assert.False(BuildFingerprint.TryLoadFile(Path.Combine(Path.GetTempPath(), "progenitor-no-such-file.json"), out _, out var missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public void FileNameIsTheAddressLibraryKey()
    {
        var fingerprint = Fingerprint(side: "server");
        Assert.Equal("build-server-" + ClientBuild + ".json", fingerprint.FileName);
        Assert.Equal("server-" + ClientBuild + ".json", fingerprint.AddressTableFileName);
        Assert.Equal(BuildFingerprint.FileNameFor("server", ClientBuild), fingerprint.FileName);
    }

    [Fact]
    public void TheNewestStoredFingerprintForThisSideIsTheBaseline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "progenitor-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var older = Fingerprint(build: ClientBuild, captured: "2026-08-01T00:00:00Z");
            var newer = Fingerprint(build: NextBuild, captured: "2026-09-01T00:00:00Z");
            var otherSide = Fingerprint(build: "999-0-0-0", captured: "2026-09-30T00:00:00Z", side: "server");
            foreach (var f in new[] { older, newer, otherSide })
                File.WriteAllText(Path.Combine(directory, f.FileName), f.ToJson());

            var notes = new List<string>();
            var baseline = ResilienceCheck.LoadBaseline(directory, "client", out var file, notes);

            Assert.NotNull(baseline);
            Assert.Equal(NextBuild, baseline!.Build);
            Assert.Equal("client", baseline.Side);
            Assert.Equal(newer.FileName, file);
            Assert.Empty(notes);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AnEmptyStoreFolderIsNoBaselineAndNoNote()
    {
        var notes = new List<string>();
        Assert.Null(ResilienceCheck.LoadBaseline(
            Path.Combine(Path.GetTempPath(), "progenitor-absent-" + Guid.NewGuid().ToString("N")),
            "client", out var file, notes));
        Assert.Equal("", file);
        Assert.Empty(notes);
    }

    // ---------------------------------------------------------------- metadata scan

    [Fact]
    public void TheScannerReadsRealMetadataAndFiltersToGameAssemblies()
    {
        // The test assembly is the only PE the suite may assume exists. It references no
        // ProjectM/Unity assembly (the csproj forbids it), so the filtered result must be empty
        // while the scan itself succeeds - that is the "no false positives" half of the check.
        var path = typeof(ResilienceTests).Assembly.Location;
        var rows = MetadataScan.ScanReferences(path, out var error);

        Assert.Null(error);
        Assert.Empty(rows);

        // Widen the filter to xunit and the same reader must find real rows with real signatures.
        var xunitRows = MetadataScan.ScanReferences(path, out var wideError, new[] { "xunit" });
        Assert.Null(wideError);
        Assert.NotEmpty(xunitRows);
        Assert.Contains(xunitRows, r => r.Kind == MemberKind.Type);
        Assert.Contains(xunitRows, r => r.Kind == MemberKind.Method && r.Signature.Contains('('));
        Assert.All(xunitRows, r => Assert.StartsWith("xunit", r.Assembly, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReferencesResolveAgainstTheAssembliesOnDisk()
    {
        var path = typeof(ResilienceTests).Assembly.Location;
        var directory = Path.GetDirectoryName(path)!;
        var references = MetadataScan.ScanReferences(path, out _, new[] { "xunit" });
        var resolved = MetadataScan.Resolve(references, directory);

        Assert.Equal(references.Count, resolved.Count);
        Assert.Contains(resolved, r => r.Status == BindingStatus.Bound);
        // Nothing may come back TypeMissing: these assemblies sit next to the test DLL.
        Assert.DoesNotContain(resolved, r => r.Status == BindingStatus.TypeMissing);
    }

    [Fact]
    public void AnAbsentAssemblyLeavesRowsNotCheckedRatherThanBroken()
    {
        var references = new MemberBinding[]
        {
            new("ProjectM", "ProjectM.Ghost", "Call", MemberKind.Method, "System.Void()", BindingStatus.NotChecked),
        };
        var resolved = MetadataScan.Resolve(references, Path.Combine(Path.GetTempPath(), "no-interop-here"));

        var row = Assert.Single(resolved);
        Assert.Equal(BindingStatus.NotChecked, row.Status);
        Assert.Contains("not readable", row.Found, StringComparison.Ordinal);
        Assert.Empty(ResilienceDiff.Compare(null, Fingerprint(members: resolved)).MembersUnbound);
    }

    [Fact]
    public void GameAssemblyPrefixFilterIsPrefixMatchedNotSubstring()
    {
        Assert.True(MetadataScan.IsGameAssembly("ProjectM"));
        Assert.True(MetadataScan.IsGameAssembly("ProjectM.Shared"));
        Assert.True(MetadataScan.IsGameAssembly("Unity.Entities"));
        Assert.True(MetadataScan.IsGameAssembly("Stunlock.Core"));
        Assert.False(MetadataScan.IsGameAssembly("Il2CppProjectM"));
        Assert.False(MetadataScan.IsGameAssembly("System.Runtime"));
        Assert.False(MetadataScan.IsGameAssembly(""));
        Assert.False(MetadataScan.IsGameAssembly(null));
    }

    // ---------------------------------------------------------------- report shape

    [Fact]
    public void TheReportIsCappedSoOneUpdateCannotFloodTheLog()
    {
        var before = new List<MemberBinding>();
        var after = new List<MemberBinding>();
        for (int i = 0; i < 40; i++)
        {
            before.Add(Bound("ProjectM.Type" + i.ToString("D2"), "Call"));
            after.Add(Broken("ProjectM.Type" + i.ToString("D2"), "Call", BindingStatus.MemberMissing));
        }

        var current = Fingerprint(build: NextBuild, members: after);
        var diff = ResilienceDiff.Compare(Fingerprint(members: before), current);
        var text = ResilienceReport.Format(diff, current);
        var lines = text.Split('\n');

        Assert.Equal(40, diff.MembersLost.Count);
        Assert.Contains("... " + (40 - ResilienceReport.MaxDetailLines) + " more", text, StringComparison.Ordinal);
        Assert.True(lines.Length < 30, "the block must stay readable, got " + lines.Length + " lines");
        Assert.All(lines, line => Assert.False(line.EndsWith(" ", StringComparison.Ordinal)));
    }

    [Fact]
    public void FormatSurvivesNulls()
    {
        Assert.Contains("no report", ResilienceReport.Format(null, null), StringComparison.Ordinal);
        Assert.Contains("no report", ResilienceReport.Format(ResilienceDiff.Compare(null, Fingerprint()), null), StringComparison.Ordinal);
    }

    // ------------------------------------------------ R31 finding 5: the fast path is a cache

    /// <summary>A stand-in GameAssembly.dll: any bytes, since the build id tolerates a non-PE.</summary>
    private static string TempBinary(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "progenitor-gameasm-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static BuildFingerprint GameFingerprint(
        string build, long length, string sha, string modified, DateTime hashed) =>
        new(
            "client", build, "2026-09-04T09:00:00Z", "0.1.0.0", "", length, sha, "",
            Array.Empty<AssemblyFingerprint>(), false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<MemberBinding>(),
            modified,
            hashed.ToString("O", CultureInfo.InvariantCulture));

    private static ResilienceOptions GameOnlyOptions(string path, string fakeHash) => new()
    {
        Side = "client",
        GameAssemblyPath = path,
        InteropDirectory = "",
        AddressTableDirectory = "",
        ScanMembers = false,
        WriteStore = false,
        UseLengthAndTimeFastPath = true,
        HashFile = _ => fakeHash,
    };

    [Fact]
    public void AnInPlacePatchUnderTheSameBuildIdStillGetsRehashed()
    {
        // The build id hashes size + PE header + the first 64 KiB, so "build id unchanged"
        // implies "length unchanged": before R31 finding 5 the fast path reused the stored hash
        // whenever the id matched, which made GameAssemblyChangedUnderSameBuildId unreachable in
        // the game (the only place the fast path is on).
        var path = TempBinary(4096);
        try
        {
            var build = AddressId.ComputeBuildIdFromFile(path);
            var length = new FileInfo(path).Length;
            var modified = new FileInfo(path).LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
            var options = GameOnlyOptions(path, "2222");

            // Length, build id, mtime and a fresh hash stamp all agree: a real cache hit.
            var stored = GameFingerprint(build, length, "1111", modified, DateTime.UtcNow);
            var hit = ResilienceCheck.Capture(options, stored, new List<string>());
            Assert.Equal("1111", hit.GameAssemblySha256);
            Assert.False(ResilienceDiff.Compare(stored, hit).GameAssemblyChangedUnderSameBuildId);

            // Same build id and length, different mtime: the file was written, so re-hash.
            var moved = GameFingerprint(build, length, "1111", "2020-01-01T00:00:00.0000000Z", DateTime.UtcNow);
            var current = ResilienceCheck.Capture(options, moved, new List<string>());
            Assert.Equal("2222", current.GameAssemblySha256);

            var diff = ResilienceDiff.Compare(moved, current);
            Assert.False(diff.BuildChanged);
            Assert.True(diff.GameAssemblyChangedUnderSameBuildId);
            Assert.False(diff.Unchanged);
            Assert.Contains("SAME build id", ResilienceReport.Format(diff, current), StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheFullHashIsRecomputedOnScheduleEvenWhenNothingLooksDifferent()
    {
        var path = TempBinary(4096);
        try
        {
            var build = AddressId.ComputeBuildIdFromFile(path);
            var info = new FileInfo(path);
            var modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
            var options = GameOnlyOptions(path, "2222");
            options.FullHashInterval = TimeSpan.FromHours(24);

            // Everything the fast path looks at matches - including the mtime, which a patcher
            // can forge. The schedule is what still forces the full hash.
            var stale = GameFingerprint(build, info.Length, "1111", modified, DateTime.UtcNow.AddHours(-48));
            var current = ResilienceCheck.Capture(options, stale, new List<string>());

            Assert.Equal("2222", current.GameAssemblySha256);
            Assert.True(ResilienceDiff.Compare(stale, current).GameAssemblyChangedUnderSameBuildId);

            var day = TimeSpan.FromHours(24);
            var now = DateTime.UtcNow;
            Assert.False(ResilienceCheck.FullHashDue(
                GameFingerprint(build, info.Length, "1111", modified, now.AddHours(-1)), day, now));
            Assert.True(ResilienceCheck.FullHashDue(null, day, now));
            // A fingerprint stored before the stamp field existed carries no hashedUtc.
            Assert.True(ResilienceCheck.FullHashDue(
                new BuildFingerprint("client", build, "", "", "", info.Length, "1111", "",
                    Array.Empty<AssemblyFingerprint>(), false, Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<MemberBinding>(), modified, ""),
                day, now));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ------------------------------------- R31 finding 7: generic instantiations are scanned

    private sealed class GenericFake<T>
    {
        public void Use(T value) => GC.KeepAlive(value);
    }

    [Fact]
    public void AMemberReachedThroughAGenericInstantiationIsScannedNotDropped()
    {
        // `list.Add(x)` compiles to a MemberRef whose parent is a TypeSpec (List`1<...>).
        // Those rows used to be dropped with no row and no count, which is why 696 references
        // could read "all bind" while none of Core's Entities/Collections surface was checked.
        var path = typeof(ResilienceTests).Assembly.Location;
        var rows = MetadataScan.ScanReferences(path, out var error, new[] { "System" });

        Assert.Null(error);
        var generic = rows.Where(r => r.Kind != MemberKind.Type && r.Type.Contains('`')).ToList();
        Assert.NotEmpty(generic);
        Assert.Contains(generic, r => r.Type == "System.Collections.Generic.List`1" && r.Name == "Add");
        // The row is keyed on the open type, and its signature uses that type's own parameters -
        // exactly what the TypeDef side renders, so the two are comparable.
        Assert.Contains(generic, r => r.Signature.Contains("!0", StringComparison.Ordinal));
        Assert.All(generic, r => Assert.NotEqual(MetadataScan.UnresolvedAssembly, r.Assembly));
    }

    [Fact]
    public void AMemberOnAGenericDefinitionResolvesAgainstTheAssemblyOnDisk()
    {
        var location = typeof(ResilienceTests).Assembly.Location;
        var directory = Path.GetDirectoryName(location)!;
        var assembly = typeof(ResilienceTests).Assembly.GetName().Name!;
        const string type = "Progenitor.Tests.ResilienceTests/GenericFake`1";

        var resolved = MetadataScan.Resolve(new MemberBinding[]
        {
            new(assembly, type, "Use", MemberKind.Method, "System.Void(!0)", BindingStatus.NotChecked),
            new(assembly, type, "Use", MemberKind.Method, "System.Void(System.Int32)", BindingStatus.NotChecked),
            new(assembly, type, "Vanished", MemberKind.Method, "System.Void(!0)", BindingStatus.NotChecked),
        }, directory);

        Assert.Equal(BindingStatus.Bound, resolved.Single(r => r.Signature == "System.Void(!0)" && r.Name == "Use").Status);
        Assert.Equal(BindingStatus.SignatureChanged, resolved.Single(r => r.Signature == "System.Void(System.Int32)").Status);
        Assert.Equal(BindingStatus.MemberMissing, resolved.Single(r => r.Name == "Vanished").Status);
    }

    [Fact]
    public void ARowTheScannerAlreadyExplainedKeepsItsReason()
    {
        var resolved = MetadataScan.Resolve(new MemberBinding[]
        {
            new(MetadataScan.UnresolvedAssembly, "!0", "Add", MemberKind.Method, "",
                BindingStatus.NotChecked, "TypeSpec parent blob did not decode"),
        }, Path.Combine(Path.GetTempPath(), "no-interop-here"));

        var row = Assert.Single(resolved);
        Assert.Equal(BindingStatus.NotChecked, row.Status);
        Assert.Contains("TypeSpec parent", row.Found, StringComparison.Ordinal);
    }

    // ------------------------------------- R31 finding 11: "all bind" needs something checked

    [Fact]
    public void AScanThatCheckedNothingIsNotReportedAsAllBind()
    {
        var rows = new[]
        {
            Broken("ProjectM.A", "One", BindingStatus.NotChecked, found: "ProjectM.dll not readable in C:\\interop"),
            Broken("ProjectM.B", "Two", BindingStatus.NotChecked),
        };
        var current = Fingerprint(members: rows);
        var diff = ResilienceDiff.Compare(Fingerprint(members: rows), current);

        Assert.Equal(2, diff.MembersNotChecked);
        Assert.Equal(0, diff.MembersChecked);
        Assert.True(diff.NothingChecked);
        Assert.False(diff.Unchanged);
        Assert.True(diff.HasBreakage);

        var text = ResilienceReport.Format(diff, current);
        Assert.DoesNotContain("all bind", text, StringComparison.Ordinal);
        Assert.Contains("NONE checked", text, StringComparison.Ordinal);
        Assert.Contains("not readable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APartlyCheckedScanNamesTheCheckedCount()
    {
        var current = Fingerprint(members: new[]
        {
            Bound("ProjectM.Sys", "Update"),
            Broken("ProjectM.Sys", "Opaque", BindingStatus.NotChecked),
        });
        var diff = ResilienceDiff.Compare(current, current);

        Assert.True(diff.Unchanged);
        Assert.False(diff.HasBreakage);
        var text = ResilienceReport.Format(diff, current);
        Assert.Contains("1 checked, all bind", text, StringComparison.Ordinal);
        Assert.Contains("1 not checked", text, StringComparison.Ordinal);
    }

    // ------------------------- R31 finding 12: the pass is canceled and joined on Uninstall

    [Fact]
    public void UninstallCancelsAndJoinsTheBackgroundPass()
    {
        var directory = Path.Combine(Path.GetTempPath(), "progenitor-resilience-" + Guid.NewGuid().ToString("N"));
        var interop = Path.Combine(directory, "interop");
        Directory.CreateDirectory(interop);
        File.WriteAllBytes(Path.Combine(interop, "ProjectM.dll"), new byte[64]);

        var entered = new ManualResetEventSlim(false);
        var logs = new List<string>();
        ResilienceOptions options = null!;
        options = new ResilienceOptions
        {
            Side = "client",
            GameAssemblyPath = Path.Combine(directory, "GameAssembly.dll"), // absent on purpose
            InteropDirectory = interop,
            AddressTableDirectory = directory,
            StoreDirectory = directory,
            ScanMembers = false,
            WriteStore = true,
            // A hasher that never finishes on its own: the only way out is the token.
            HashFile = _ =>
            {
                entered.Set();
                while (!options.Cancellation.IsCancellationRequested) Thread.Sleep(5);
                options.Cancellation.ThrowIfCancellationRequested();
                return "";
            },
        };

        try
        {
            ResilienceModule.LogSink = text => { lock (logs) logs.Add(text); };
            Assert.True(ResilienceModule.Install(options));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the pass never reached the hasher");

            var watch = Stopwatch.StartNew();
            ResilienceModule.Uninstall();
            watch.Stop();

            // Bounded: Uninstall must not return before the thread is gone, and must not wait
            // forever either. The join budget is the ceiling.
            Assert.True(
                watch.ElapsedMilliseconds < ResilienceModule.UninstallJoinMilliseconds + 2000,
                "Uninstall took " + watch.ElapsedMilliseconds + " ms");

            // A canceled pass logs nothing and stores nothing - it must not become the next
            // run's baseline, and it must not write into the next generation's log.
            Thread.Sleep(200);
            lock (logs) Assert.Empty(logs);
            Assert.Empty(Directory.GetFiles(directory, "build-*.json"));

            // State is clean: a new generation can install.
            Assert.True(ResilienceModule.Install(new ResilienceOptions
            {
                GameAssemblyPath = Path.Combine(directory, "GameAssembly.dll"),
                InteropDirectory = interop,
                StoreDirectory = directory,
                ScanMembers = false,
                WriteStore = false,
            }));
        }
        finally
        {
            ResilienceModule.Uninstall();
            ResilienceModule.LogSink = null;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------- R32 finding 7: the full-file hash is chunked, and Uninstall does not wait for it

    /// <summary>
    /// A stream that hands out one chunk per <see cref="Read"/> and runs a hook first, so a test
    /// can cancel between chunks and count how many reads the hasher managed. No file and no real
    /// clock: the per-read delay stands in for a cold-cache read of GameAssembly.dll.
    /// </summary>
    private sealed class ChunkedFakeStream : Stream
    {
        private readonly Action<int> _beforeRead;
        private long _remaining;

        public ChunkedFakeStream(long length, Action<int> beforeRead)
        {
            _remaining = length;
            _beforeRead = beforeRead;
        }

        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            _beforeRead(Reads);
            int n = (int)Math.Min(count, _remaining);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ChunkedHashMatchesAOneShotSha256()
    {
        // Chunking must not change the answer. A length that is not a multiple of the chunk
        // exercises the final short read.
        var bytes = new byte[(ResilienceCheck.HashChunkBytes * 2) + 1234];
        new Random(1234).NextBytes(bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var stream = new MemoryStream(bytes, writable: false);
        Assert.Equal(expected, ResilienceCheck.Sha256Stream(stream, CancellationToken.None));
    }

    [Fact]
    public void ChunkedHashCancelledMidStreamStopsWithinOneChunk()
    {
        // 64 chunks at 100 ms each is ~6.4 s of hashing - the shape of a cold-cache pass over
        // GameAssembly.dll, which SHA256.ComputeHash(stream) used to sit through uncancellably
        // while Uninstall blocked the game main thread waiting on it (R32 finding 7).
        using var cancel = new CancellationTokenSource();
        var stream = new ChunkedFakeStream(
            ResilienceCheck.HashChunkBytes * 64L,
            read =>
            {
                Thread.Sleep(100);
                if (read == 1) cancel.Cancel(); // canceled while the first chunk is in flight
            });

        var watch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(
            () => ResilienceCheck.Sha256Stream(stream, cancel.Token));
        watch.Stop();

        // One chunk in flight, then out: the token is checked before every read, so the second
        // read never happens and the call costs one chunk, not the other 63.
        Assert.Equal(1, stream.Reads);
        // Reads == 1 is the proof; the time only has to stay clear of the full 6.4 s pass.
        Assert.True(watch.ElapsedMilliseconds < 4000, "the hash took " + watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void UninstallReturnsWithinTheJoinBoundWhenThePassIgnoresTheToken()
    {
        // The pathological case the bound exists for: a pass that does not observe cancellation
        // at all. Uninstall cancels, waits its short budget, and returns - the game main thread
        // is never held for the rest of the work (R32 finding 7).
        var directory = Path.Combine(Path.GetTempPath(), "progenitor-resilience-" + Guid.NewGuid().ToString("N"));
        var interop = Path.Combine(directory, "interop");
        Directory.CreateDirectory(interop);
        File.WriteAllBytes(Path.Combine(interop, "ProjectM.dll"), new byte[64]);

        var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var logs = new List<string>();
        var options = new ResilienceOptions
        {
            Side = "client",
            GameAssemblyPath = Path.Combine(directory, "GameAssembly.dll"), // absent on purpose
            InteropDirectory = interop,
            AddressTableDirectory = directory,
            StoreDirectory = directory,
            ScanMembers = false,
            WriteStore = true,
            // Deliberately token-blind, like the old uncancellable ComputeHash.
            HashFile = _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return "";
            },
        };

        try
        {
            ResilienceModule.LogSink = text => { lock (logs) logs.Add(text); };
            Assert.True(ResilienceModule.Install(options));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the pass never reached the hasher");

            var watch = Stopwatch.StartNew();
            ResilienceModule.Uninstall();
            watch.Stop();

            // The budget plus scheduling slack, and nowhere near the 30 s the hasher would take.
            Assert.True(
                watch.ElapsedMilliseconds < ResilienceModule.UninstallJoinMilliseconds + 1000,
                "Uninstall took " + watch.ElapsedMilliseconds + " ms");

            // The thread was still running when Uninstall returned, and it is silent: the
            // generation is gone and the sink is null, so the late finish logs and stores nothing.
            release.Set();
            Thread.Sleep(300);
            lock (logs) Assert.Empty(logs);
            Assert.Empty(Directory.GetFiles(directory, "build-*.json"));
        }
        finally
        {
            release.Set();
            ResilienceModule.Uninstall();
            ResilienceModule.LogSink = null;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // --------------------- R31 finding 19: this build's stored fingerprint is the baseline

    [Fact]
    public void ThisBuildsStoredFingerprintIsPreferredOverTheNewestOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), "progenitor-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var onA = Fingerprint(build: ClientBuild, captured: "2026-08-01T00:00:00Z");
            var onB = Fingerprint(build: NextBuild, captured: "2026-09-01T00:00:00Z");
            foreach (var f in new[] { onA, onB })
                File.WriteAllText(Path.Combine(directory, f.FileName), f.ToJson());

            // Back on build A after a beta detour: A is compared with A, not with the newer B,
            // so an A/B/A alternation stops reporting "build CHANGED" on every single run.
            var notes = new List<string>();
            var baseline = ResilienceCheck.LoadBaseline(directory, "client", out var file, notes, ClientBuild);
            Assert.Equal(ClientBuild, baseline!.Build);
            Assert.Equal(onA.FileName, file);
            Assert.False(ResilienceDiff.Compare(baseline, Fingerprint(build: ClientBuild)).BuildChanged);

            // A build with nothing stored still falls back to the newest for the side.
            var fallback = ResilienceCheck.LoadBaseline(directory, "client", out var newestFile, notes, "0-0-0-nothing");
            Assert.Equal(NextBuild, fallback!.Build);
            Assert.Equal(onB.FileName, newestFile);
            Assert.Empty(notes);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // --------------------- job FUPD5: which folder the pass actually writes to

    /// <summary>
    /// A fake install: the folder tree the pass reads, with no game in it. The point of the whole
    /// job is the second argument to <see cref="ResilienceModule.ResolveCoreFolder"/> — in the
    /// game the first one is empty, because Bootstrap loads Core from bytes.
    /// </summary>
    private sealed class FakeInstall : IDisposable
    {
        public FakeInstall(bool withCoreFolder = true, bool withCoreDll = true, bool withAddressLib = true)
        {
            GameRoot = Path.Combine(Path.GetTempPath(), "progenitor-fupd5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(GameRoot);
            File.WriteAllText(Path.Combine(GameRoot, ResilienceCheck.GameAssemblyFileName), "not a real GameAssembly");
            Interop = Path.Combine(GameRoot, ResilienceModule.BepInExFolderName, "interop");
            Directory.CreateDirectory(Interop);
            CoreFolder = Path.Combine(GameRoot, ResilienceModule.BepInExFolderName, ResilienceModule.CoreFolderName);
            if (withCoreFolder) Directory.CreateDirectory(CoreFolder);
            CoreDll = Path.Combine(CoreFolder, ResilienceModule.CoreFileName);
            if (withCoreFolder && withCoreDll)
                File.Copy(typeof(FakeInstall).Assembly.Location, CoreDll, overwrite: true);
            AddressLib = Path.Combine(CoreFolder, ResilienceCheck.TableFolderName);
            if (withCoreFolder && withAddressLib) Directory.CreateDirectory(AddressLib);
        }

        public string GameRoot { get; }
        public string Interop { get; }
        public string CoreFolder { get; }
        public string CoreDll { get; }
        public string AddressLib { get; }

        public void Dispose()
        {
            try { Directory.Delete(GameRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ACoreLoadedFromBytesStillResolvesItsFolderFromTheGameRoot()
    {
        using var install = new FakeInstall();

        // Assembly.Location is "" for an assembly loaded with LoadFromStream, which is exactly how
        // Bootstrap loads Core. The old code took Path.GetDirectoryName of that, got "", and every
        // downstream folder was empty.
        var folder = ResilienceModule.ResolveCoreFolder("", install.GameRoot, Directory.Exists, out var origin);

        Assert.Equal(install.CoreFolder, folder);
        Assert.Contains(ResilienceModule.CoreFolderName, origin, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", origin, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealAssemblyLocationStillWinsSoTheOfflineRunnerIsUnchanged()
    {
        using var install = new FakeInstall();
        string elsewhere = Path.Combine(install.GameRoot, "somewhere", ResilienceModule.CoreFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "x");

        var folder = ResilienceModule.ResolveCoreFolder(elsewhere, install.GameRoot, Directory.Exists, out var origin);

        Assert.Equal(Path.GetDirectoryName(elsewhere), folder);
        Assert.Contains("loaded from", origin, StringComparison.Ordinal);
    }

    [Fact]
    public void AGameRootWithoutTheFolderYetStillNamesItAndSaysSo()
    {
        using var install = new FakeInstall(withCoreFolder: false);

        var folder = ResilienceModule.ResolveCoreFolder("", install.GameRoot, Directory.Exists, out var origin);

        Assert.Equal(install.CoreFolder, folder);
        Assert.Contains("does not exist yet", origin, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLocationAndNoGameRootIsUnresolvedNotSilent()
    {
        var folder = ResilienceModule.ResolveCoreFolder("", "", Directory.Exists, out var origin);

        Assert.Equal("", folder);
        Assert.Equal("unresolved", origin);
        Assert.Contains("could not be resolved", ResilienceModule.UnresolvedCoreFolderWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMemberScanFallsBackToTheCoreDllInTheResolvedFolder()
    {
        using var install = new FakeInstall();

        Assert.Equal(
            install.CoreDll,
            ResilienceModule.ResolveCoreAssemblyPath("", install.CoreFolder, File.Exists));
        Assert.Equal(
            @"C:\real\Progenitor.dll",
            ResilienceModule.ResolveCoreAssemblyPath(@"C:\real\Progenitor.dll", install.CoreFolder, File.Exists));

        using var empty = new FakeInstall(withCoreDll: false);
        Assert.Equal("", ResilienceModule.ResolveCoreAssemblyPath("", empty.CoreFolder, File.Exists));
        Assert.Equal("", ResilienceModule.ResolveCoreAssemblyPath("", "", File.Exists));
    }

    [Fact]
    public void TheReportSaysWhichFolderItResolvedAndHow()
    {
        var text = ResilienceReport.Format(
            ResilienceDiff.Compare(null, Fingerprint()),
            Fingerprint(),
            archive: null,
            store: ResilienceCheck.FormatStoreLine(
                @"C:\g\BepInEx\progenitor-core\addresslib",
                "game folder + BepInEx/progenitor-core; addresslib subfolder"));

        Assert.Contains("store", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\g\BepInEx\progenitor-core\addresslib", text, StringComparison.Ordinal);
        Assert.Contains("addresslib subfolder", text, StringComparison.Ordinal);

        // The unchanged block carries the row too: that is the block a healthy server prints.
        Assert.Contains(
            @"C:\g\store",
            ResilienceReport.Format(
                ResilienceDiff.Compare(Fingerprint(), Fingerprint()),
                Fingerprint(),
                archive: null,
                store: ResilienceCheck.FormatStoreLine(@"C:\g\store", "")),
            StringComparison.Ordinal);

        // No store line given (a caller formatting a diff on its own): the block is as it was.
        Assert.DoesNotContain(
            "  store ",
            ResilienceReport.Format(ResilienceDiff.Compare(null, Fingerprint()), Fingerprint()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvableStoreFolderIsALoudLineNotAForeverFirstRun()
    {
        var result = ResilienceCheck.Run(new ResilienceOptions
        {
            Side = "server",
            GameAssemblyPath = "",
            InteropDirectory = "",
            AddressTableDirectory = "",
            StoreDirectory = "",
            StoreDirectoryOrigin = "unresolved",
            ScanMembers = false,
            WriteStore = true,
        });

        Assert.Equal("", result.StoredPath);
        Assert.Contains(ResilienceCheck.StoreUnresolved, result.Report, StringComparison.Ordinal);
        Assert.Contains(ResilienceCheck.StoreUnresolvedNote, result.Notes);
    }

    [Fact]
    public void WithTheFolderResolvedThePassStoresScansAndSaysWhere()
    {
        using var install = new FakeInstall();
        // The interop folder holds one real assembly so the member scan has something to read.
        File.Copy(
            typeof(FakeInstall).Assembly.Location,
            Path.Combine(install.Interop, Path.GetFileName(typeof(FakeInstall).Assembly.Location)),
            overwrite: true);

        string folder = ResilienceModule.ResolveCoreFolder("", install.GameRoot, Directory.Exists, out var origin);
        string store = Path.Combine(folder, ResilienceCheck.TableFolderName);
        var result = ResilienceCheck.Run(new ResilienceOptions
        {
            Side = "server",
            GameAssemblyPath = Path.Combine(install.GameRoot, ResilienceCheck.GameAssemblyFileName),
            InteropDirectory = install.Interop,
            AddressTableDirectory = folder,
            StoreDirectory = store,
            StoreDirectoryOrigin = origin + "; " + ResilienceCheck.TableFolderName + " subfolder",
            CoreAssemblyPath = ResilienceModule.ResolveCoreAssemblyPath("", folder, File.Exists),
            ScanMembers = true,
            WriteStore = true,
        });

        Assert.Equal(Path.Combine(store, result.Current.FileName), result.StoredPath);
        Assert.True(File.Exists(result.StoredPath));
        Assert.Contains(store, result.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceCheck.StoreUnresolved, result.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(ResilienceCheck.StoreUnresolvedNote, result.Notes);

        // Second pass: the fingerprint the first one wrote is now the baseline, which is the whole
        // symptom this job fixes (every Core generation said "first run").
        var again = ResilienceCheck.Run(new ResilienceOptions
        {
            Side = "server",
            GameAssemblyPath = Path.Combine(install.GameRoot, ResilienceCheck.GameAssemblyFileName),
            InteropDirectory = install.Interop,
            AddressTableDirectory = folder,
            StoreDirectory = store,
            ScanMembers = false,
            WriteStore = true,
        });
        Assert.True(again.Diff.HasBaseline);
        Assert.DoesNotContain("first run", again.Report, StringComparison.Ordinal);
    }

    // --------------------------------------------- FUPD6: who the archive is on for (R53 #9)

    private static string WriteConfig(string bepInExRoot, string body)
    {
        var configFolder = Path.Combine(bepInExRoot, "config");
        Directory.CreateDirectory(configFolder);
        var path = Path.Combine(configFolder, InteropArchive.ConfigFileName);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void TheArchiveIsOffUnlessSomeoneSaidOtherwise()
    {
        // FUPD4 shipped it on for everyone: ~180 MB per build, ~540 MB per game root at the
        // default retention, inside a stranger's Steam install, for a developer tool that is not
        // even in the package. The rule now is the one Reload.WatchCore already set.
        var off = InteropArchivePolicy.Decide(configSet: false, configValue: false, devTree: false);
        Assert.False(off.Archive);
        Assert.Equal(InteropArchivePolicy.ReasonDefaultOff, off.Reason);
        Assert.Contains("540 MB", off.Reason);
        Assert.Contains("driftmap", off.Reason);

        var dev = InteropArchivePolicy.Decide(configSet: false, configValue: false, devTree: true);
        Assert.True(dev.Archive);
        Assert.Equal(InteropArchivePolicy.ReasonDevTree, dev.Reason);

        // An explicit setting wins in both directions, including inside a developer tree: an
        // operator who typed false meant false.
        var on = InteropArchivePolicy.Decide(configSet: true, configValue: true, devTree: false);
        Assert.True(on.Archive);
        Assert.Equal(InteropArchivePolicy.ReasonConfigOn, on.Reason);

        var refused = InteropArchivePolicy.Decide(configSet: true, configValue: false, devTree: true);
        Assert.False(refused.Archive);
        Assert.Equal(InteropArchivePolicy.ReasonConfigOff, refused.Reason);

        Assert.False(InteropArchivePolicy.DefaultArchiveInterop);
    }

    [Fact]
    public void TheDevTreeSignalIsTheOneCoreWatchPolicyAlreadyUses()
    {
        // tools/deploy.ps1 and tools/stage-test-server.ps1 write [Reload] WatchCore = true into
        // our own client and test-server config and refuse to write it anywhere else, so that key
        // being true IS the machine-readable "this install is one of ours". Reused, not reinvented.
        Assert.Equal("Reload", InteropArchivePolicy.DevTreeSection);
        Assert.Equal("WatchCore", InteropArchivePolicy.DevTreeKey);

        var root = Path.Combine(Path.GetTempPath(), "progenitor-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bepInEx = Path.Combine(root, "BepInEx");

            // Nothing on disk at all: off, and it is not an error.
            Assert.False(InteropArchivePolicy.Decide(bepInEx).Archive);

            WriteConfig(bepInEx, "[Reload]\nWatchCore = true\n");
            var dev = InteropArchivePolicy.Decide(bepInEx);
            Assert.True(dev.Archive);
            Assert.Equal(InteropArchivePolicy.ReasonDevTree, dev.Reason);

            WriteConfig(bepInEx, "[Reload]\nWatchCore = false\n");
            Assert.False(InteropArchivePolicy.Decide(bepInEx).Archive);

            // A player's install that never heard of WatchCore, with the archive asked for by hand.
            WriteConfig(bepInEx, "[Resilience]\nArchiveInterop = true\n");
            var explicitOn = InteropArchivePolicy.Decide(bepInEx);
            Assert.True(explicitOn.Archive);
            Assert.Equal(InteropArchivePolicy.ReasonConfigOn, explicitOn.Reason);

            // Explicit false beats the dev tree.
            WriteConfig(bepInEx, "[Reload]\nWatchCore = true\n[Resilience]\nArchiveInterop = false\n");
            Assert.False(InteropArchivePolicy.Decide(bepInEx).Archive);

            // A typo is "nothing was said", never a deliberate true that writes 540 MB.
            WriteConfig(bepInEx, "[Resilience]\nArchiveInterop = yes please\n");
            var junk = InteropArchivePolicy.Decide(bepInEx);
            Assert.False(junk.Archive);
            Assert.Equal(InteropArchivePolicy.ReasonDefaultOff, junk.Reason);

            // A key in the wrong section is not the key.
            WriteConfig(bepInEx, "[Resilience]\nWatchCore = true\n");
            Assert.False(InteropArchivePolicy.Decide(bepInEx).Archive);

            // A commented-out line is not a setting.
            WriteConfig(bepInEx, "[Reload]\n# WatchCore = true\n");
            Assert.False(InteropArchivePolicy.Decide(bepInEx).Archive);

            // The retention key still reads out of the same file next to the new one.
            WriteConfig(bepInEx, "[Reload]\nWatchCore = true\n[Resilience]\nInteropArchives = 5\n");
            Assert.Equal(5, InteropArchive.ReadRetainFromConfig(bepInEx));
            Assert.True(InteropArchivePolicy.Decide(bepInEx).Archive);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------ FUPD7: the two keys reach the .cfg an operator opens (R54 #9)

    [Fact]
    public void BothArchiveKeysAreBoundSoTheirDescriptionsReachTheConfigFile()
    {
        // Neither key was ever bound, so BepInEx never wrote either one — or either description —
        // into progenitor.cfg, and both ConfigDescription constants were prose no human
        // path could reach. The bind is main-thread and documentation-only; the pass goes on
        // reading the file by hand, because it runs on a background thread in a game-free file.
        var source = SourcePin.ReadFile("src", "Progenitor.Core", "Resilience", "InteropArchiveConfig.cs");
        SourcePin.AssertStatement(source, "ConfigHelper.Bind(");
        SourcePin.AssertUncommented(source, "InteropArchivePolicy.ConfigKey");
        SourcePin.AssertUncommented(source, "InteropArchivePolicy.ConfigDescription");
        SourcePin.AssertUncommented(source, "InteropArchive.ConfigKey");
        SourcePin.AssertUncommented(source, "InteropArchive.ConfigDescription");

        // And it is called on the main thread, before the pass thread exists.
        var install = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Resilience", "Resilience.cs"),
            "public static bool Install(ResilienceOptions? options = null)");
        SourcePin.AssertStatementExactly(install, "InteropArchiveConfig.Bind();");
    }

    [Fact]
    public void TheBoundDefaultIsAutoBecauseFalseWouldBeADecision()
    {
        // The trap in binding this key at all: Decide reads a WRITTEN key as an operator decision
        // that beats the developer-tree signal in both directions, so a bool bind's default of
        // false would have turned the archive off in every developer tree the moment BepInEx first
        // wrote the file. Documenting the switch would have disabled it.
        Assert.Equal("auto", InteropArchivePolicy.ConfigValueAuto);
        Assert.False(bool.TryParse(InteropArchivePolicy.ConfigValueAuto, out _));

        var source = SourcePin.ReadFile("src", "Progenitor.Core", "Resilience", "InteropArchiveConfig.cs");
        SourcePin.AssertUncommented(source, "InteropArchivePolicy.ConfigValueAuto");
        SourcePin.AssertNoStatementContaining(source, "DefaultArchiveInterop");

        // Written as the bind writes it, the decision is the one an unwritten key gets.
        var root = Path.Combine(Path.GetTempPath(), "progenitor-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bepInEx = Path.Combine(root, "BepInEx");
            WriteConfig(bepInEx, "[Resilience]\nArchiveInterop = " + InteropArchivePolicy.ConfigValueAuto
                                 + "\nInteropArchives = " + InteropArchive.DefaultRetain + "\n");
            var auto = InteropArchivePolicy.Decide(bepInEx);
            Assert.False(auto.Archive);
            Assert.Equal(InteropArchivePolicy.ReasonDefaultOff, auto.Reason);
            Assert.Equal(InteropArchive.DefaultRetain, InteropArchive.ReadRetainFromConfig(bepInEx));

            // The developer tree still turns itself on with the key sitting there at its default.
            WriteConfig(bepInEx, "[Reload]\nWatchCore = true\n[Resilience]\nArchiveInterop = "
                                 + InteropArchivePolicy.ConfigValueAuto + "\n");
            var dev = InteropArchivePolicy.Decide(bepInEx);
            Assert.True(dev.Archive);
            Assert.Equal(InteropArchivePolicy.ReasonDevTree, dev.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheDescriptionsSayWhatTheOperatorIsChoosingBetween()
    {
        // They are now the text a player reads in their own .cfg, which is what R53 finding 9
        // asked for and what an unreferenced constant could never deliver.
        foreach (var value in new[] { "auto", "true", "false", "540 MB", "driftmap", "WatchCore" })
            Assert.Contains(value, InteropArchivePolicy.ConfigDescription, StringComparison.Ordinal);

        foreach (var value in new[] { "180 MB", "540 MB", "driftmap", "1..10", "ArchiveInterop" })
            Assert.Contains(value, InteropArchive.ConfigDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportRowSaysWhyTheArchiveIsOff()
    {
        var fingerprint = Fingerprint();
        var diff = ResilienceDiff.Compare(null, fingerprint);

        // Off with a reason is a row: an operator who needs an --old input six weeks from now
        // should find out now, not by not having one.
        var explained = ResilienceReport.Format(diff, fingerprint,
            InteropArchiveResult.None(InteropArchiveStatus.Disabled, InteropArchivePolicy.ReasonDefaultOff));
        var row = explained.Split('\n').Single(l => l.TrimStart().StartsWith("archive", StringComparison.Ordinal));
        Assert.Contains("off - ", row);
        Assert.Contains("not a developer tree", row);

        // A caller that simply never asked for an archive keeps the block it always had.
        Assert.DoesNotContain("archive",
            ResilienceReport.Format(diff, fingerprint,
                    InteropArchiveResult.None(InteropArchiveStatus.Disabled))
                .Split('\n').Select(l => l.Trim().Split(' ')[0]));

        Assert.Equal("interop archive off: " + InteropArchivePolicy.ReasonDefaultOff,
            InteropArchivePolicy.FormatNote(new InteropArchiveDecision(false, InteropArchivePolicy.ReasonDefaultOff)));
        Assert.Equal("interop archive on: " + InteropArchivePolicy.ReasonDevTree,
            InteropArchivePolicy.FormatNote(new InteropArchiveDecision(true, InteropArchivePolicy.ReasonDevTree)));
    }

    [Fact]
    public void TheDisabledReasonReachesTheBlockThroughTheWholePass()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var interop = Path.Combine(root, "interop");
            var store = Path.Combine(root, "store");
            Directory.CreateDirectory(interop);
            File.WriteAllText(Path.Combine(interop, "ProjectM.dll"), "one");
            var gameAssembly = Path.Combine(root, "GameAssembly.dll");
            File.WriteAllBytes(gameAssembly, new byte[] { 1, 2, 3, 4 });

            var result = ResilienceCheck.Run(new ResilienceOptions
            {
                Side = "client",
                GameAssemblyPath = gameAssembly,
                InteropDirectory = interop,
                StoreDirectory = store,
                ScanMembers = false,
                WriteStore = false,
                ArchiveInterop = false,
                ArchiveDisabledReason = InteropArchivePolicy.ReasonDefaultOff,
            });

            Assert.Equal(InteropArchiveStatus.Disabled, result.Archive!.Status);
            Assert.Equal(InteropArchivePolicy.ReasonDefaultOff, result.Archive.Reason);
            Assert.Contains("off - not a developer tree", result.Report);
            Assert.DoesNotContain(Directory.GetDirectories(root),
                d => Path.GetFileName(d)!.StartsWith("interop-", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
