using System;
using System.Linq;
using System.Text;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SELFFIX1 (user report 2026-10-07: HotDemo installed from the join window within seconds, yet the join had been refused
/// at once, so a rejoin was needed): the client's mod report claims the required mods it can fix by itself (a copy it
/// turns on now, a hot-loadable package of its local test source), and the server's install-window check treats a claim
/// for a key it requires as fixable. Untrusted: it only earns the window. Game-free; not live-verified.
/// </summary>
[Collection("ModManifest")]
public sealed class SelfFixClaimTests
{
    private static PackageVersionAvailability NotListed(string package, string version, NetworkVersionRule rule) =>
        PackageVersionAvailability.NotListed;

    private static (ClientModVerdict Verdict, ClientPolicyConfig Policy) Required(string id = "progenitor.hotdemo", string version = "0.1.0",
        ClientModRule rule = ClientModRule.AtLeast)
    {
        var policy = new ClientPolicyConfig();
        policy.Required.Add(new ClientModRow(id, "", version, rule));
        var verdict = ClientModJudge.Judge(policy, new ClientModReport(), new ClientModServerFacts());
        Assert.Equal(ClientJudgmentOutcome.Refuse, verdict.Outcome);
        return (verdict, policy);
    }

    [Fact]
    public void The_report_carries_bounded_claims_and_an_older_report_has_none()
    {
        var entries = new[] { new ClientModEntry("a.mod", "A", "1.0", ClientModSources.Chainloader) };
        string plain = Encoding.UTF8.GetString(ClientModReportWire.Body(entries, false, out _));
        Assert.DoesNotContain("fixes", plain);

        var claims = Enumerable.Range(0, 40).Select(i => new ClientModFix("m" + i, ClientModFix.KindTurnOn, "1.0")).ToList();
        claims.Insert(0, new ClientModFix("bad.kind", "install-anything", "1.0"));
        claims.Insert(1, new ClientModFix(new string('k', ClientModFix.MaxKeyChars + 1), ClientModFix.KindLocalTest, ""));
        byte[] body = ClientModReportWire.Body(entries, false, claims, out _);
        Assert.True(ClientModReportWire.TryParse(body, out var report, out var error), error);
        Assert.Single(report.Entries);
        Assert.Equal(ClientModFix.MaxFixes, report.Fixes.Count);
        Assert.DoesNotContain(report.Fixes, f => f.Kind == "install-anything");
        Assert.Equal("m0", report.Fixes[0].Key);

        // An older server reads the same body: the mods are all it looks at.
        Assert.True(ClientModReportWire.TryParse(Encoding.UTF8.GetBytes(plain), out var old, out _));
        Assert.Empty(old.Fixes);
    }

    /// <summary>The live case: a required mod with no Thunderstore package, and the client's local test source offers it.</summary>
    [Fact]
    public void A_claimed_local_fix_for_a_required_key_opens_the_window()
    {
        var (verdict, policy) = Required();
        var report = new ClientModReport();
        Assert.False(InstallWindowFixes.PolicyFixable(verdict, policy, report, NotListed, out _, out string why));
        Assert.Contains("claims no fix", why);

        report.Fixes.Add(new ClientModFix("progenitor.hotdemo", ClientModFix.KindLocalTest, "0.1.0"));
        Assert.True(InstallWindowFixes.PolicyFixable(verdict, policy, report, NotListed, out var keys, out var kinds, out why), why);
        Assert.Equal(new[] { "progenitor.hotdemo" }, keys);
        Assert.Equal(new[] { MissFix.LocalTestSource }, kinds);
        Assert.Equal("local-test (claimed)", InstallWindowFixes.FixName(kinds[0]));

        var turnOn = new ClientModReport();
        turnOn.Fixes.Add(new ClientModFix("progenitor.hotdemo", ClientModFix.KindTurnOn, "0.2.0"));
        Assert.True(InstallWindowFixes.PolicyFixable(verdict, policy, turnOn, NotListed, out _, out kinds, out _));
        Assert.Equal(MissFix.TurnOn, kinds[0]);
    }

    /// <summary>A claim earns nothing for a key the server does not require, or at a version its rule refuses.</summary>
    [Fact]
    public void A_claim_for_another_key_or_a_refused_version_earns_nothing()
    {
        var (verdict, policy) = Required(rule: ClientModRule.Exact);
        var other = new ClientModReport();
        other.Fixes.Add(new ClientModFix("someone.else", ClientModFix.KindLocalTest, "0.1.0"));
        Assert.False(InstallWindowFixes.PolicyFixable(verdict, policy, other, NotListed, out _, out _));

        var wrongVersion = new ClientModReport();
        wrongVersion.Fixes.Add(new ClientModFix("progenitor.hotdemo", ClientModFix.KindLocalTest, "0.0.9"));
        Assert.False(InstallWindowFixes.PolicyFixable(verdict, policy, wrongVersion, NotListed, out _, out _));

        var noVersion = new ClientModReport();
        noVersion.Fixes.Add(new ClientModFix("progenitor.hotdemo", ClientModFix.KindTurnOn, ""));
        Assert.False(InstallWindowFixes.PolicyFixable(verdict, policy, noVersion, NotListed, out _, out _));
    }

    /// <summary>The claim is only a claim: the verdict on the real report is unchanged by it.</summary>
    [Fact]
    public void A_claim_never_makes_the_report_pass()
    {
        var policy = new ClientPolicyConfig();
        policy.Required.Add(new ClientModRow("progenitor.hotdemo", "", "0.1.0", ClientModRule.AtLeast));
        var report = new ClientModReport();
        report.Fixes.Add(new ClientModFix("progenitor.hotdemo", ClientModFix.KindLocalTest, "0.1.0"));
        Assert.Equal(ClientJudgmentOutcome.Refuse, ClientModJudge.Judge(policy, report, new ClientModServerFacts()).Outcome);
    }

    // ---- review fixes ---------------------------------------------------------------------------

    private static string LocalIndex(string dir, params string[] versions)
    {
        var sb = new StringBuilder("[{\"name\":\"HotDemo\",\"full_name\":\"Example-HotDemo\",\"owner\":\"Example\",\"package_url\":\"\",\"is_deprecated\":false,\"categories\":[\"Mods\"],\"versions\":[");
        for (int i = 0; i < versions.Length; i++)
        {
            string zip = System.IO.Path.Combine(dir, "Example-HotDemo-" + versions[i] + ".zip");
            System.IO.File.WriteAllText(zip, "placeholder");
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"HotDemo\",\"full_name\":\"Example-HotDemo-" + versions[i] + "\",\"description\":\"d\",\"version_number\":\"" + versions[i]
                + "\",\"dependencies\":[],\"download_url\":\"" + new Uri(zip).AbsoluteUri + "\",\"file_size\":100,\"is_active\":true}");
        }

        sb.Append("]}]");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, LocalIndexSource.IndexFileName), sb.ToString());
        return dir;
    }

    /// <summary>
    /// Claims name only the remembered policy's required mods, at the version its row accepts; with no remembered policy
    /// (a first join) every candidate, newest version. Bounded versions are neither sent nor read.
    /// </summary>
    [Fact]
    public void Claims_follow_the_remembered_policy_and_its_version_rule()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "progenitor-selffix-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        LocalTestSource.ResetForTests();
        var savedFolder = JoinFlowClient.LocalTestFolderProbe;
        var savedFacts = JoinFlowClient.LocalTestGuidsProbe;
        try
        {
            LocalIndex(dir, "0.1.0", "0.2.0");
            JoinFlowClient.LocalTestFolderProbe = () => dir;
            JoinFlowClient.LocalTestGuidsProbe = _ => new LocalZipFacts("progenitor.hotdemo", true, Array.Empty<string>());

            var exact = new[] { new ClientModRow("progenitor.hotdemo", "", "0.1.0", ClientModRule.Exact) };
            var claim = Assert.Single(JoinFlowClient.SelfFixClaims(exact));
            Assert.Equal(ClientModFix.KindLocalTest, claim.Kind);
            Assert.Equal("0.1.0", claim.Version);

            // Review: a local test source claim outside the known rows is still sent (bounded), at the newest version: a
            // requirement the admins added since the remembered policy keeps its window.
            Assert.Equal("0.2.0", Assert.Single(JoinFlowClient.SelfFixClaims(new[] { new ClientModRow("someone.else", "", "1.0", ClientModRule.Exact) })).Version);
            Assert.Empty(JoinFlowClient.SelfFixClaims(new[] { new ClientModRow("progenitor.hotdemo", "", "0.3.0", ClientModRule.Exact) }));
            Assert.Equal("0.2.0", Assert.Single(JoinFlowClient.SelfFixClaims(null)).Version);

            // This join's asked rows count with the remembered ones.
            JoinFlowClient.AskedGuidsSource = () => new[] { "progenitor.hotdemo" };
            JoinFlowClient.RememberedRequiredSource = () => new[] { new ClientModRow("someone.else", "", "1.0", ClientModRule.Exact) };
            var rows = JoinFlowClient.CurrentRequiredRows()!;
            Assert.Contains(rows, r => r.Names("progenitor.hotdemo"));
            Assert.Contains(rows, r => r.Names("someone.else"));
            JoinFlowClient.AskedGuidsSource = () => Array.Empty<string>();
            JoinFlowClient.RememberedRequiredSource = null;
            Assert.Null(JoinFlowClient.CurrentRequiredRows());
            JoinFlowClient.AskedGuidsSource = null;

            // Not hot-loadable: installing it would not fix the join in this session, so it is not claimed.
            LocalTestSource.ResetForTests();
            JoinFlowClient.LocalTestGuidsProbe = _ => new LocalZipFacts("progenitor.hotdemo", false, Array.Empty<string>());
            Assert.Empty(JoinFlowClient.SelfFixClaims(null));
        }
        finally
        {
            JoinFlowClient.LocalTestFolderProbe = savedFolder;
            JoinFlowClient.AskedGuidsSource = null;
            JoinFlowClient.RememberedRequiredSource = null;
            JoinFlowClient.LocalTestGuidsProbe = savedFacts;
            LocalTestSource.ResetForTests();
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }

        var longVersion = new ClientModFix("a.mod", ClientModFix.KindTurnOn, new string('9', ClientModFix.MaxVersionChars + 1));
        string body = Encoding.UTF8.GetString(ClientModReportWire.Body(Array.Empty<ClientModEntry>(), false, new[] { longVersion }, out _));
        Assert.DoesNotContain("fixes", body);
    }

    /// <summary>
    /// Review (MEDIUM): a report with claims is not "changed" on the next mid-session check (which compares the mods
    /// alone), so the 10 s tick neither pauses nor resends nor rebuilds the claims for the whole session.
    /// </summary>
    [Fact]
    public void A_report_with_claims_is_not_changed_on_the_next_check()
    {
        ClientModReportClient.ResetForTests();
        try
        {
            ClientModReportClient.TestObserved.Add(new ClientModObservation("a.mod", "A", "1.0", ClientModSources.Chainloader));
            int built = 0;
            ClientModReportClient.FixesProbe = () =>
            {
                built++;
                return new[] { new ClientModFix("progenitor.hotdemo", ClientModFix.KindLocalTest, "0.1.0") };
            };
            Assert.True(ClientModReportClient.Send("test", force: true) > 0);
            var changed = typeof(ClientModReportClient).GetMethod("ChangedSinceLastSend",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert.False((bool)changed.Invoke(null, null)!);
            Assert.Equal(1, built);
            Assert.Equal(0, ClientModReportClient.Send("again", force: false));
        }
        finally { ClientModReportClient.ResetForTests(); }
    }
}
