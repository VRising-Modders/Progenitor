using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW2, game-free: the exact-version lookup, the row actions with what Thunderstore serves, the batch
/// (bounded, a failure isolated), the Join now / restart decision, the Thunderstore confirmation and its record,
/// the rejoin record (write, expiry, single use, no password), the relaunch command, and the reconnect.
/// </summary>
public sealed class JoinFlowStage2Tests
{
    private const string A = "team.alpha";
    private const string B = "team.beta";

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static ManifestMod Row(string id, string version, string? package = null, bool shares = false, bool hot = false) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, hot, null, package,
            shares ? new string('c', 64) : null, shares ? 1234 : null);

    private static ManifestBody Catalog(params ManifestMod[] rows) =>
        new(1, "Night Garden", "2026-10-04T12:00:00Z", new ManifestOperator("Ada", "", "a1b2c3d4e5f67890"), null, rows);

    /// <summary>A Thunderstore v1 index: one package, versions newest first, an inactive one among them.</summary>
    private static string Index(string package = "Owner-Alpha", string host = "https://thunderstore.io") =>
        "[{\"name\":\"" + package.Split('-')[1] + "\",\"full_name\":\"" + package + "\",\"owner\":\"" + package.Split('-')[0] + "\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/x/\",\"versions\":["
        + V(package, "1.3.0", true, host) + "," + V(package, "1.2.0", true, host) + "," + V(package, "1.1.0", false, host) + "]}]";

    private static string V(string package, string version, bool active, string host) =>
        "{\"name\":\"x\",\"full_name\":\"" + package + "-" + version + "\",\"version_number\":\"" + version + "\",\"description\":\"\","
        + "\"dependencies\":[],\"download_url\":\"" + host + "/package/download/" + package.Replace('-', '/') + "/" + version + "/\","
        + "\"downloads\":1,\"date_created\":\"2026-01-01T00:00:00Z\",\"is_active\":" + (active ? "true" : "false") + ",\"file_size\":4000}";

    private static Dictionary<string, PackageVersions> Load(string json, string package, string version)
    {
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ThunderstoreVersions.TryLoad(json, "s", new Dictionary<string, string> { [package] = version }, into, false, out string error), error);
        return into;
    }

    // ------------------------------------------------------------ version resolution

    [Fact]
    public void The_exact_version_is_resolved_never_a_neighbor()
    {
        var listed = new[] { "1.3.0", "1.2.0", "1.10.0" };
        Assert.Equal("1.2.0", ThunderstoreVersions.Resolve(listed, "1.2.0"));
        // A BepInEx four-part version and a two-part one are the same version, part by part.
        Assert.Equal("1.2.0", ThunderstoreVersions.Resolve(listed, "1.2.0.0"));
        Assert.Equal("1.2.0", ThunderstoreVersions.Resolve(listed, "1.2"));
        Assert.Null(ThunderstoreVersions.Resolve(listed, "1.1.0"));
        Assert.Null(ThunderstoreVersions.Resolve(listed, "1.2.1"));
        // A non-numeric version must match exactly.
        Assert.Null(ThunderstoreVersions.Resolve(new[] { "1.2.0-beta" }, "1.2.0"));
        Assert.Equal("1.2.0-beta", ThunderstoreVersions.Resolve(new[] { "1.2.0-beta" }, "1.2.0-beta"));
        Assert.Null(ThunderstoreVersions.Resolve(Array.Empty<string>(), "1.0.0"));
    }

    [Fact]
    public void The_lookup_honors_the_mods_version_rule_like_the_handshake()
    {
        var listed = new[] { "1.3.0", "1.2.7", "1.2.5", "1.1.0" };
        // Exact: only that version (a four-part x.y.z.0 is Thunderstore's x.y.z).
        Assert.Null(ThunderstoreVersions.Resolve(listed, "1.2.6", NetworkVersionRule.Exact, out string note));
        Assert.Equal("", note);
        Assert.Equal("1.2.5", ThunderstoreVersions.Resolve(listed, "1.2.5.0", NetworkVersionRule.Exact, out note));
        Assert.Equal("", note);
        Assert.Null(ThunderstoreVersions.Resolve(listed, "1.2.5.4", NetworkVersionRule.Exact, out _));
        // Minor: the exact one when listed, else the newest of the same major.minor, and it says so.
        Assert.Equal("1.2.5", ThunderstoreVersions.Resolve(listed, "1.2.5", NetworkVersionRule.Minor, out note));
        Assert.Equal("", note);
        Assert.Equal("1.2.7", ThunderstoreVersions.Resolve(listed, "1.2.6", NetworkVersionRule.Minor, out note));
        Assert.Equal("1.2.7 (the server accepts any 1.2.x; 1.2.6 is not listed)", note);
        Assert.Equal("1.2.7", ThunderstoreVersions.Resolve(listed, "1.2.5.4", NetworkVersionRule.Minor, out _));
        Assert.Null(ThunderstoreVersions.Resolve(listed, "1.4.0", NetworkVersionRule.Minor, out _));
        // A pre-release is not the same minor as a release (the handshake's own SameMinor).
        Assert.Null(ThunderstoreVersions.Resolve(new[] { "1.2.3" }, "1.2.0-beta", NetworkVersionRule.Minor, out _));
        // A local index may list oldest first: the highest is taken, not the first.
        Assert.Equal("1.2.7", ThunderstoreVersions.Resolve(new[] { "1.2.0", "1.2.7", "1.2.5" }, "1.2.6", NetworkVersionRule.Minor, out _));
        // None: presence only.
        Assert.Equal("1.3.0", ThunderstoreVersions.Resolve(listed, "9.9.9", NetworkVersionRule.None, out note));
        Assert.StartsWith("1.3.0 (the server accepts any version", note, StringComparison.Ordinal);
        // A rule this build does not know is never treated as None.
        Assert.Null(ThunderstoreVersions.Resolve(listed, "9.9.9", (NetworkVersionRule)5, out _));

        // The page model and the version lists follow the rule too.
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ThunderstoreVersions.TryLoad(Index(), "s", new Dictionary<string, string> { ["Owner-Alpha"] = "1.2.9" }, into, false,
            new Dictionary<string, NetworkVersionRule> { ["Owner-Alpha"] = NetworkVersionRule.Minor }, out _));
        Assert.Equal("1.2.0", into["Owner-Alpha"].Matched);
        Assert.Equal(PackageVersionAvailability.Available, ThunderstoreVersions.Availability(into, "Owner-Alpha", "1.2.9", NetworkVersionRule.Minor));
        Assert.Equal(PackageVersionAvailability.VersionGone, ThunderstoreVersions.Availability(into, "Owner-Alpha", "1.2.9"));
        var minor = Handshake.ModRecord.CreateDeclared(A, "1.2.9", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor);
        var model = Model(new[] { minor }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2.9", "Owner-Alpha")), into);
        Assert.Equal(JoinRowAction.Install, model.Required[0].Action);
        Assert.Equal(NetworkVersionRule.Minor, JoinFlowSession.RequestFor(model.Required[0]).VersionRule);
    }

    [Fact]
    public void An_older_version_is_read_from_the_version_list_with_its_own_download()
    {
        var loaded = Load(Index(), "Owner-Alpha", "1.2.0");
        var pv = loaded["Owner-Alpha"];
        Assert.Equal(new[] { "1.3.0", "1.2.0" }, pv.Active);
        Assert.Equal("1.3.0", pv.Latest);
        Assert.Equal("1.2.0", pv.Matched);
        Assert.Equal("1.2.0", pv.Entry!.Manifest.Version);
        Assert.EndsWith("/Owner/Alpha/1.2.0/", pv.Entry.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal("Owner-Alpha", pv.Entry.Key);
        Assert.Equal(PackageVersionAvailability.Available, ThunderstoreVersions.Availability(loaded, "Owner-Alpha", "1.2.0"));

        // An inactive version is gone, and so is one never published; a package not listed is not listed.
        var gone = Load(Index(), "Owner-Alpha", "1.1.0");
        Assert.Null(gone["Owner-Alpha"].Entry);
        Assert.Equal(PackageVersionAvailability.VersionGone, gone["Owner-Alpha"].Availability);
        Assert.Equal(PackageVersionAvailability.VersionGone, ThunderstoreVersions.Availability(gone, "Owner-Alpha", "0.9.0"));
        Assert.Equal(PackageVersionAvailability.NotListed, ThunderstoreVersions.Availability(gone, "Owner-Beta", "1.0.0"));
        Assert.Equal(PackageVersionAvailability.Unknown, ThunderstoreVersions.Availability(null, "Owner-Alpha", "1.0.0"));
    }

    [Fact]
    public void The_local_index_shadows_the_fetched_listing_and_a_bad_body_is_a_failure_not_a_throw()
    {
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        var wanted = new Dictionary<string, string> { ["Owner-Alpha"] = "1.3.0" };
        Assert.True(ThunderstoreVersions.TryLoad(Index(host: "file:///C:/dummy"), "local", wanted, into, true, out _));
        Assert.True(ThunderstoreVersions.TryLoad(Index(), "fetched", wanted, into, false, out _));
        Assert.True(into["Owner-Alpha"].Local);
        Assert.StartsWith("file:", into["Owner-Alpha"].Entry!.DownloadUrl, StringComparison.Ordinal);
        Assert.False(ThunderstoreVersions.TryLoad("{nope", "x", wanted, new Dictionary<string, PackageVersions>(), false, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void The_adapter_still_picks_the_newest_active_version_without_a_pin()
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(Index(), "s", out var entries, out _));
        Assert.Equal("1.3.0", Assert.Single(entries).Manifest.Version);
    }

    // ------------------------------------------------------------ row actions

    private static JoinPageModel Model(Handshake.ModRecord[] asked, Handshake.ModRecord[] provided, ManifestBody catalog,
        IReadOnlyDictionary<string, PackageVersions>? versions, JoinSection? section = null) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = asked,
            Provided = provided,
            Catalog = catalog,
            Section = section,
            ThunderstoreVersions = versions,
        });

    [Theory]
    [InlineData("Missing", true, "Unknown", "Install")]
    [InlineData("Missing", true, "Available", "Install")]
    [InlineData("Older", true, "Available", "Update")]
    [InlineData("Missing", true, "VersionGone", "AskAdmin")]
    [InlineData("Missing", true, "NotListed", "AskAdmin")]
    [InlineData("Newer", true, "Available", "UseVersion")]
    [InlineData("Newer", true, "VersionGone", "TellAdmin")]
    [InlineData("Newer", true, "Unknown", "TellAdmin")]
    [InlineData("Newer", false, "Unknown", "TellAdmin")]
    [InlineData("Ready", true, "Available", "Ready")]
    [InlineData("ServerLacks", true, "Available", "TurnOff")]
    public void Row_actions_follow_what_Thunderstore_serves(string statusName, bool package, string availabilityName, string expectedName)
    {
        var status = Enum.Parse<JoinRowStatus>(statusName);
        var availability = Enum.Parse<PackageVersionAvailability>(availabilityName);
        var origin = status == JoinRowStatus.ServerLacks ? JoinRowOrigin.YouRequire : JoinRowOrigin.ServerRequires;
        Assert.Equal(Enum.Parse<JoinRowAction>(expectedName), JoinPageModel.ActionFor(origin, status, package, availability));
    }

    [Fact]
    public void Being_ahead_offers_the_servers_exact_older_version_with_its_caption_and_restart_tag()
    {
        var versions = Load(Index(), "Owner-Alpha", "1.2.0");
        var model = Model(new[] { Must(A, "1.2.0") }, new[] { Must(A, "1.3.0") }, Catalog(Row(A, "1.2.0", "Owner-Alpha")), versions);
        var row = model.Required[0];
        Assert.Equal(JoinRowAction.UseVersion, row.Action);
        Assert.Equal("Use version 1.2.0", JoinText.ActionCaption(row));
        Assert.Equal("use-version", JoinText.ActionName(row.Action));
        Assert.True((row.Tags & JoinRowTags.NeedsRestart) != 0);
        Assert.Equal("1.3.0", row.ThunderstoreLatest);
        Assert.Contains(A, model.InstallTargets(null));
        Assert.Equal(JoinInstallKind.UseVersion, JoinFlowSession.RequestFor(row).Kind);
        Assert.Equal("1.3.0", JoinFlowSession.RequestFor(row).Replaces);
        Assert.Contains("replaced by 1.2.0", JoinText.UseVersionWarning(row.Name, "1.3.0", "1.2.0"), StringComparison.Ordinal);

        // Without the version on Thunderstore it stays "Tell the admin".
        var gone = Model(new[] { Must(A, "1.1.0") }, new[] { Must(A, "1.3.0") }, Catalog(Row(A, "1.1.0", "Owner-Alpha")), Load(Index(), "Owner-Alpha", "1.1.0"));
        Assert.Equal(JoinRowAction.TellAdmin, gone.Required[0].Action);
    }

    [Fact]
    public void A_gone_version_asks_the_admins_and_says_so_plainly()
    {
        var versions = Load(Index(), "Owner-Alpha", "1.1.0");
        var model = Model(new[] { Must(A, "1.1.0") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.1.0", "Owner-Alpha")), versions);
        var row = model.Required[0];
        Assert.Equal(JoinRowAction.AskAdmin, row.Action);
        Assert.True(row.VersionGone);
        Assert.Contains("no longer on Thunderstore", JoinText.CannotFixFooter(model), StringComparison.Ordinal);
        var s = new JoinFlowSession(JoinMode.Refused, model, new Fx());
        Assert.Equal("version-gone", s.RowAction(A));
        Assert.Equal("Alpha 1.1.0 is no longer on Thunderstore (it has 1.3.0). Ask the admins to update the server.", s.Notice);
    }

    // ------------------------------------------------------------ the session

    private sealed class Fx : IJoinFlowEffects
    {
        public readonly List<string> Calls = new();
        public bool Trust = true;
        public Dictionary<string, JoinInstallRowState> States = new(StringComparer.OrdinalIgnoreCase);

        public bool CopyToClipboard(string text) => true;

        public void Leave() => Calls.Add("leave");
        public void Closed(bool completed) => Calls.Add("closed:" + completed);
        public void MarkSeen() { }
        public void PageShown(JoinPage page) { }
        public bool NeedsThunderstoreTrust(IReadOnlyList<JoinInstallRequest> requests) => Trust;
        public bool KeySame = true;
        public bool ThunderstoreKeyUnchanged(JoinThunderstoreFacts shown) => KeySame;

        public string StartThunderstore(IReadOnlyList<JoinInstallRequest> requests, bool acknowledge)
        {
            Calls.Add("ts:" + string.Join(",", requests.Select(r => r.PackageVersion)) + (acknowledge ? ":ack" : ""));
            return "Installing";
        }

        public JoinInstallRowState? InstallState(string key) => States.TryGetValue(key, out var s) ? s : null;
        public string JoinNow() => "joining";
        public string RestartAndRejoin(bool dryRun) => dryRun ? "dry-run" : "quitting";
        public string CancelRejoin() => "canceled";
    }

    [Fact]
    public void Install_N_mods_confirms_the_thunderstore_rows_once_then_starts_them_with_the_checked_recommended_rows()
    {
        var section = new JoinSection("", null, new[] { new RecommendedMod("maps.mod", "Owner-Maps", "2.0.0", "Maps") }, null);
        var model = Model(new[] { Must(A, "1.2.0"), Must(B, "1") }, new[] { Must(B, "0.9") },
            Catalog(Row(A, "1.2.0", "Owner-Alpha"), Row(B, "1", "Owner-Beta")), null, section);
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        s.ToggleRecommended("maps.mod");
        Assert.Equal(3, s.InstallTargets.Count);
        Assert.Equal("ts-trust", s.InstallSelected());
        Assert.Equal(JoinPage.ThunderstoreTrust, s.Page);
        Assert.Equal(new[] { "Owner-Alpha@1.2.0", "Owner-Beta@1", "Owner-Maps@2.0.0" }, s.TsTrust!.Requests.Select(r => r.PackageVersion));
        Assert.Equal("Install 3 mods from Thunderstore that Night Garden lists?", JoinText.TsTrustIntro(s.TsTrust.ServerName, 3));
        Assert.Empty(fx.Calls);
        Assert.Equal("install", s.TsTrustInstall());
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal(new[] { "ts:Owner-Alpha@1.2.0,Owner-Beta@1,Owner-Maps@2.0.0:ack" }, fx.Calls);
        Assert.Equal("Installing", s.Notice);

        // A key that already confirmed every package installs at once; a row the batch is handling is left out.
        var fx2 = new Fx { Trust = false };
        fx2.States[A] = new JoinInstallRowState(JoinFlowSession.RequestFor(model.Required.First(r => r.Id == A))) { Phase = JoinInstallPhase.Downloading };
        var t = new JoinFlowSession(JoinMode.Refused, model, fx2);
        Assert.Equal(new[] { B }, t.InstallTargets);
        Assert.Equal("install", t.InstallSelected());
        Assert.Equal(new[] { "ts:Owner-Beta@1" }, fx2.Calls);
        Assert.Equal("busy", t.RowAction(A));
        Assert.Equal(JoinText.BatchBusy, t.Notice);
    }

    [Fact]
    public void Back_on_the_thunderstore_confirmation_installs_nothing_and_escape_is_back()
    {
        var model = Model(new[] { Must(A, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2.0", "Owner-Alpha")), null);
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        Assert.Equal("ts-trust", s.RowAction(A));
        Assert.Equal("declined", s.Escape());
        Assert.Equal(JoinText.NothingInstalled, s.Notice);
        Assert.Empty(fx.Calls);

        // Another key landed while the prompt was open: shown again, nothing started or recorded.
        fx.KeySame = false;
        s.RowAction(A);
        Assert.Equal("changed", s.TsTrustInstall());
        Assert.Equal(JoinPage.ThunderstoreTrust, s.Page);
        Assert.Equal(JoinText.TrustOfferChanged, s.TrustError);
        Assert.Empty(fx.Calls);
        fx.KeySame = true;
        Assert.Equal("install", s.TsTrustInstall());
        Assert.Single(fx.Calls);
    }

    [Fact]
    public void The_result_page_offers_join_now_or_restart_and_later_closes()
    {
        var model = Model(new[] { Must(A, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2.0", "Owner-Alpha")), null);
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        Assert.Equal("result", s.ShowResult(new JoinResultOffer { Kind = JoinAfterInstall.RestartNeeded, CanRejoin = true, Summary = "1 mod loads after a restart." }));
        Assert.Equal(JoinPage.Result, s.Page);
        Assert.Equal(new[] { JoinFlowLayout.ButtonLater, JoinFlowLayout.ButtonRestart }, JoinFlowLayout.ResultButtonKeys(s.Result!));
        Assert.Equal(JoinText.RestartBody, JoinFlowLayout.ResultBody(s.Result!));
        Assert.Equal("join now is not offered", s.ResultJoinNow());
        Assert.Equal("dry-run", s.ResultRestart(dryRun: true));
        Assert.Equal("closed", s.ResultLater());
        Assert.Equal(new[] { "closed:False" }, fx.Calls);

        var manual = new JoinResultOffer { Kind = JoinAfterInstall.RestartNeeded, CanRejoin = true, ManualRelaunch = true };
        Assert.EndsWith(JoinText.RestartManual, JoinFlowLayout.ResultBody(manual), StringComparison.Ordinal);
        var hosted = new JoinResultOffer { Kind = JoinAfterInstall.RestartNeeded, Hosted = true };
        Assert.Equal(JoinText.RestartHosted, JoinFlowLayout.ResultBody(hosted));
        Assert.Equal(new[] { JoinFlowLayout.ButtonLater }, JoinFlowLayout.ResultButtonKeys(hosted));
        Assert.Equal(JoinText.CloseButton, JoinFlowLayout.ResultButtons(hosted).Single().Caption);

        var t = new JoinFlowSession(JoinMode.Refused, model, new Fx());
        t.ShowResult(new JoinResultOffer { Kind = JoinAfterInstall.JoinNow, CanRejoin = true });
        Assert.Equal(new[] { JoinFlowLayout.ButtonLater, JoinFlowLayout.ButtonJoinNow }, JoinFlowLayout.ResultButtonKeys(t.Result!));
        Assert.Equal("joining", t.ResultJoinNow());
        // A batch that ends while a trust prompt is open never pushes it aside.
        var u = new JoinFlowSession(JoinMode.Refused, model, new Fx());
        u.RowAction(A);
        Assert.Equal("noted", u.ShowResult(new JoinResultOffer { Kind = JoinAfterInstall.JoinNow, Summary = "Loaded 1 mod." }));
        Assert.Equal(JoinPage.ThunderstoreTrust, u.Page);
    }

    [Fact]
    public void A_failed_or_refused_row_counts_again_and_its_reason_leads_its_line()
    {
        var model = Model(new[] { Must(A, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2.0", "Owner-Alpha")), null);
        var fx = new Fx();
        var state = new JoinInstallRowState(JoinFlowSession.RequestFor(model.Required[0])) { Phase = JoinInstallPhase.Refused, Reason = "It is a server-only mod" };
        fx.States[A] = state;
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        // A refusal is a rule: the row is not offered again this join (live 2026-10-05: "Install 2 mods" still counted it).
        Assert.Empty(s.InstallTargets);
        var spec = JoinFlowLayout.Required(model.Required[0], "", 900f, state);
        var name = spec.Cells.Single(c => c.Key == JoinFlowLayout.CellName);
        Assert.StartsWith("Refused: It is a server-only", name.NoteLead, StringComparison.Ordinal);
        var action = spec.Cells.Single(c => c.Key == JoinFlowLayout.CellAction);
        Assert.Equal("Refused", action.Text);
        Assert.NotEqual(CellKind.Button, action.Kind);

        // A failure (a download that broke) keeps its button and counts again.
        state.Phase = JoinInstallPhase.Failed;
        state.Reason = "timed out";
        Assert.Equal(new[] { A }, s.InstallTargets);
        var failed = JoinFlowLayout.Required(model.Required[0], "", 900f, state);
        Assert.StartsWith("Failed: timed out", failed.Cells.Single(c => c.Key == JoinFlowLayout.CellName).NoteLead, StringComparison.Ordinal);
        Assert.Equal("Install", failed.Cells.Single(c => c.Key == JoinFlowLayout.CellAction).Text);

        state.Phase = JoinInstallPhase.Downloading;
        state.Percent = 62;
        var running = JoinFlowLayout.Required(model.Required[0], "", 900f, state);
        Assert.Equal("Downloading 62%", running.Cells.Single(c => c.Key == JoinFlowLayout.CellAction).Text);
        state.Phase = JoinInstallPhase.AfterRestart;
        Assert.Equal("After restart", JoinFlowLayout.Required(model.Required[0], "", 900f, state).Cells.Single(c => c.Key == JoinFlowLayout.CellAction).Text);
    }

    [Fact]
    public void The_tags_follow_what_the_install_did_not_the_catalogs_prediction()
    {
        // The catalog says the mod hot-loads (applies now); the install took the restart route.
        var model = Model(new[] { Must(A, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2.0", "Owner-Alpha", hot: true)), null);
        var row = model.Required[0];
        Assert.Equal(JoinRowTags.AppliesNow, row.Tags);
        var state = new JoinInstallRowState(JoinFlowSession.RequestFor(row)) { Phase = JoinInstallPhase.AfterRestart };
        Assert.Equal(JoinRowTags.NeedsRestart, JoinText.TagsAfter(row.Tags, state));
        string drawn = JoinFlowLayout.Required(row, "", 1200f, state).Cells.Single(c => c.Key == JoinFlowLayout.CellName).Text;
        Assert.Contains("needs restart", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("applies now", drawn, StringComparison.Ordinal);
        state.Phase = JoinInstallPhase.Loaded;
        Assert.Equal(JoinRowTags.AppliesNow, JoinText.TagsAfter(JoinRowTags.NeedsRestart, state));
        Assert.Equal(JoinRowTags.AppliesNow, JoinText.TagsAfter(JoinRowTags.AppliesNow, null));
    }

    [Fact]
    public void A_refused_recommended_row_loses_its_switch_is_not_counted_and_cannot_be_checked_again()
    {
        var section = new JoinSection("", null, new[] { new RecommendedMod("srv.only", "Owner-Srv", "1.0.0", "x"), new RecommendedMod("ok.mod", "Owner-Ok", "1.0.0", "y") }, null);
        var model = Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Catalog(), null, section);
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.InWorld, model, fx);
        s.ToggleRecommended("srv.only");
        s.ToggleRecommended("ok.mod");
        Assert.Equal(2, s.InstallTargets.Count);
        var refused = new JoinInstallRowState(JoinFlowSession.RequestFor(model.Recommended[0]))
        {
            Phase = JoinInstallPhase.Refused,
            Reason = "It is a server-only mod; it does not install on your game.",
        };
        fx.States["srv.only"] = refused;
        Assert.Equal(new[] { "ok.mod" }, s.InstallTargets);
        Assert.Equal("Install 1 mod", JoinText.InstallButton(s.InstallTargets.Count));
        Assert.False(s.IsChecked(model.Recommended[0]));
        Assert.False(s.ToggleRecommended("srv.only"));
        Assert.Equal(new[] { "ok.mod" }, s.InstallTargets);

        var spec = JoinFlowLayout.Recommended(model.Recommended[0], true, "", refused);
        Assert.Equal(CellKind.Spacer, spec.Cells.Single(c => c.Key == JoinFlowLayout.CellCheck).Kind);
        Assert.Equal("Refused", spec.Cells.Single(c => c.Key == JoinFlowLayout.CellAction).Text);
        Assert.StartsWith("Refused: It is a server-only", spec.Cells.Single(c => c.Key == JoinFlowLayout.CellName).NoteLead, StringComparison.Ordinal);
        // The word fits the status column (live 2026-10-05: "Can't inst…").
        Assert.Equal("Refused", MenuPanelLayout.ClipLabel(JoinText.InstallPhaseWord(refused), JoinFlowLayout.ActionWidth, 18f));
    }

    [Fact]
    public void The_rejoin_page_body_follows_the_phase()
    {
        // Live 2026-10-05: the connect status is said once, by the status line with its percentage; the body only
        // says why after a restart, and nothing for Join now.
        var v = new JoinRejoinView { ServerName = "Night Garden", Endpoint = "127.0.0.1:9976", AfterRestart = true, Phase = "connecting", Progress = 30 };
        Assert.Equal("V Rising restarted to finish installing.", JoinFlowLayout.RejoinBodyText(v));
        Assert.Equal("Connecting to 127.0.0.1:9976… 30%", JoinFlowLayout.RejoinStatus(v));
        var now = new JoinRejoinView { ServerName = "Night Garden", Endpoint = "127.0.0.1:9976", Phase = "connecting", Progress = 30 };
        Assert.Equal("", JoinFlowLayout.RejoinBodyText(now));
        Assert.Equal("Connecting to 127.0.0.1:9976… 30%", JoinFlowLayout.RejoinStatus(now));
        now.Phase = "leaving";
        Assert.Equal("", JoinFlowLayout.RejoinBodyText(now));
        Assert.DoesNotContain("Connecting", JoinFlowLayout.RejoinBodyText(v));
        now.Phase = "password";
        now.Status = JoinText.RejoinPasswordNote;
        Assert.Equal(JoinText.RejoinPasswordNote, JoinFlowLayout.RejoinBodyText(now));
        v.Phase = "joined";
        v.Status = "Joined Night Garden.";
        Assert.Equal("Joined Night Garden.", JoinFlowLayout.RejoinBodyText(v));
        v.Phase = "failed";
        v.Status = "Could not reconnect: x. Join the server again from the server list.";
        Assert.StartsWith("Could not reconnect", JoinFlowLayout.RejoinBodyText(v), StringComparison.Ordinal);
        var summary = new RejoinSummary();
        summary.Required.Add(new KeyValuePair<string, string>("Alpha", "Ready"));
        summary.Required.Add(new KeyValuePair<string, string>("Beta", "Still missing"));
        Assert.Equal("Alpha: Ready · Beta: Still missing", JoinFlowLayout.RejoinRequiredLine(summary));
    }

    // ------------------------------------------------------------ the batch

    private sealed class GatedInstaller : IJoinPackageInstaller
    {
        public int Running;
        public readonly Dictionary<string, JoinInstallOutcome> Outcomes = new();
        public readonly SemaphoreSlim Release = new(0);

        public async Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Running);
            progress(JoinInstallPhase.Downloading, 50);
            await Release.WaitAsync(ct);
            Interlocked.Decrement(ref Running);
            if (request.Package == "O-Throws") throw new InvalidOperationException("boom");
            return Outcomes.TryGetValue(request.Package, out var o) ? o : new JoinInstallOutcome(JoinInstallPhase.Loaded);
        }
    }

    private static JoinInstallRequest Req(string package, bool required = true) =>
        new(package, package, package, "1.0.0", required ? JoinInstallKind.Install : JoinInstallKind.Recommended, required, modId: package);

    [Fact]
    public void A_batch_runs_at_most_two_at_a_time_and_a_failure_does_not_stop_the_others()
    {
        var installer = new GatedInstaller();
        installer.Outcomes["O-Two"] = new JoinInstallOutcome(JoinInstallPhase.Refused, "too large");
        installer.Outcomes["O-Three"] = new JoinInstallOutcome(JoinInstallPhase.AfterRestart);
        // The same package once; a required row wins over a recommended row naming it, whatever the order.
        var mixed = new JoinInstallBatch(new[] { Req("O-Mix", required: false), Req("O-Mix") }, installer);
        Assert.True(Assert.Single(mixed.Rows).Request.Required);
        var batch = new JoinInstallBatch(new[] { Req("O-One"), Req("O-Two"), Req("O-Throws"), Req("O-Three"), Req("O-One") }, installer);
        Assert.Equal(4, batch.Rows.Count);
        var all = batch.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref installer.Running) == 2, 5000));
        Thread.Sleep(50);
        Assert.Equal(2, Volatile.Read(ref installer.Running));
        Assert.Contains(batch.Rows, r => r.Phase == JoinInstallPhase.Downloading && r.Percent == 50);
        installer.Release.Release(10);
        Assert.True(all.Wait(5000));
        Assert.True(batch.Done);
        Assert.Equal(2, batch.MaxRunning);
        Assert.Equal(JoinInstallPhase.Loaded, batch.Row("O-One")!.Phase);
        Assert.Equal(JoinInstallPhase.Refused, batch.Row("O-Two")!.Phase);
        Assert.Equal("too large", batch.Row("O-Two")!.Reason);
        Assert.Equal(JoinInstallPhase.Failed, batch.Row("O-Throws")!.Phase);
        Assert.Equal("boom", batch.Row("O-Throws")!.Reason);
        Assert.Equal(JoinInstallPhase.AfterRestart, batch.Row("O-Three")!.Phase);
        Assert.Equal("Loaded 1 mod. 1 mod loads after a restart. Not installed: O-Two (too large); O-Throws (boom).", JoinText.BatchSummary(batch.Rows));
    }

    // ------------------------------------------------------------ Join now or restart

    private static JoinInstallRowState Done(JoinInstallRequest r, JoinInstallPhase phase) => new(r) { Phase = phase };

    [Fact]
    public void Join_now_when_every_required_mod_loaded_restart_when_any_needs_it_incomplete_when_one_is_missing()
    {
        var model = Model(new[] { Must(A, "1"), Must(B, "1") }, Array.Empty<Handshake.ModRecord>(),
            Catalog(Row(A, "1", "O-A"), Row(B, "1", "O-B")), null);
        var a = new JoinInstallRequest(A, "A", "O-A", "1", JoinInstallKind.Install, true, modId: A);
        var b = new JoinInstallRequest(B, "B", "O-B", "1", JoinInstallKind.Install, true, modId: B);
        var rec = new JoinInstallRequest("maps", "Maps", "O-Maps", "1", JoinInstallKind.Recommended, false);
        Assert.Equal(JoinAfterInstall.JoinNow, JoinAfterInstallPolicy.Decide(model, new[] { Done(a, JoinInstallPhase.Loaded), Done(b, JoinInstallPhase.Loaded) }));
        Assert.Equal(JoinAfterInstall.RestartNeeded, JoinAfterInstallPolicy.Decide(model, new[] { Done(a, JoinInstallPhase.Loaded), Done(b, JoinInstallPhase.AfterRestart) }));
        // A checked recommended mod that needs the restart asks for it too.
        Assert.Equal(JoinAfterInstall.RestartNeeded, JoinAfterInstallPolicy.Decide(model,
            new[] { Done(a, JoinInstallPhase.Loaded), Done(b, JoinInstallPhase.Loaded), Done(rec, JoinInstallPhase.AfterRestart) }));
        Assert.Equal(JoinAfterInstall.Incomplete, JoinAfterInstallPolicy.Decide(model, new[] { Done(a, JoinInstallPhase.Loaded), Done(b, JoinInstallPhase.Refused) }));
        Assert.Equal(JoinAfterInstall.Incomplete, JoinAfterInstallPolicy.Decide(model, new[] { Done(a, JoinInstallPhase.Loaded) }));
        Assert.Equal(JoinAfterInstall.Nothing, JoinAfterInstallPolicy.Decide(model, Array.Empty<JoinInstallRowState>()));
        // In the world with every required mod ready, a loaded recommended mod is Join now (the client keeps playing).
        var ready = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, Catalog(Row(A, "1", "O-A")), null);
        Assert.Equal(JoinAfterInstall.JoinNow, JoinAfterInstallPolicy.Decide(ready, new[] { Done(rec, JoinInstallPhase.Loaded) }));
    }

    // ------------------------------------------------------------ the Thunderstore confirmation

    [Fact]
    public void The_batch_confirmation_is_recorded_per_host_and_key_and_asked_again_for_a_new_package_or_key()
    {
        var file = new TrustedOperatorsFile();
        var packages = new[] { "Owner-Alpha@1.2.0", "Owner-Maps@2.0.0" };
        Assert.True(JoinThunderstoreTrust.NeedsPrompt(file, "1.2.3.4:9876", "SPKI", packages));
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var acked = ModManifestTrust.AcknowledgeThunderstore(file, "1.2.3.4:9876", "SPKI", "AB12", packages, now);
        Assert.False(JoinThunderstoreTrust.NeedsPrompt(acked, "1.2.3.4:9876", "SPKI", new[] { "owner-alpha@1.2.0" }));
        Assert.True(JoinThunderstoreTrust.NeedsPrompt(acked, "1.2.3.4:9876", "SPKI", new[] { "Owner-Alpha@1.3.0" }));
        Assert.True(JoinThunderstoreTrust.NeedsPrompt(acked, "1.2.3.4:9876", "OTHER", packages));
        Assert.True(JoinThunderstoreTrust.NeedsPrompt(acked, "5.6.7.8:9876", "SPKI", packages));
        Assert.Equal("Oct 4, 2026", JoinThunderstoreTrust.LastConfirmed(acked, "1.2.3.4:9876", "SPKI"));
        // A second batch adds to the same row.
        var more = ModManifestTrust.AcknowledgeThunderstore(acked, "1.2.3.4:9876", "SPKI", "ab12", new[] { "Owner-Beta@1.0.0" }, now.AddMinutes(1));
        Assert.Equal(3, Assert.Single(more.ThunderstoreAcks).Packages.Count);
        // No key, no record (and always a prompt).
        Assert.Same(more, ModManifestTrust.AcknowledgeThunderstore(more, "h", "", "fp", packages, now));
        Assert.True(JoinThunderstoreTrust.NeedsPrompt(more, "h", "", packages));
    }

    [Fact]
    public void The_confirmation_survives_the_trust_file_round_trip_and_every_other_trust_write_but_is_not_a_pin()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var file = ModManifestTrust.AcknowledgeThunderstore(new TrustedOperatorsFile(), "1.2.3.4:9876", "SPKI", "ab12", new[] { "Owner-Alpha@1.2.0" }, now);
        Assert.Empty(file.Operators);
        string json = ModManifestJson.CanonicalTrust(file);
        Assert.Contains("\"thunderstoreAcks\"", json, StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseTrust(json, out var parsed, out _));
        Assert.Single(parsed.ThunderstoreAcks);
        Assert.False(JoinThunderstoreTrust.NeedsPrompt(parsed, "1.2.3.4:9876", "SPKI", new[] { "Owner-Alpha@1.2.0" }));
        // An operator pin and a newer catalog stamp keep it.
        var pinned = ModManifestTrust.Upsert(parsed, "1.2.3.4:9876", "ab12", "SPKI", true, now);
        var cleared = ModManifestTrust.AdvanceIssued(pinned, "1.2.3.4:9876", "SPKI", "2026-10-05T00:00:00Z");
        Assert.Single(cleared.ThunderstoreAcks);
        // A file without one is byte-identical to before.
        Assert.DoesNotContain("thunderstoreAcks", ModManifestJson.CanonicalTrust(new TrustedOperatorsFile { Operators = pinned.Operators }), StringComparison.Ordinal);
        Assert.Empty(ModManifestTrust.ClearThunderstoreAcks(cleared, "AB12").ThunderstoreAcks);
    }

    // ------------------------------------------------------------ the rejoin record

    private static JoinTarget Target(bool password = false) => new()
    {
        Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren", ServerName = "Night Garden", Fingerprint = "ab12", NeedsPassword = password,
    };

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "progenitor-rejoin-" + Guid.NewGuid().ToString("N"), RejoinStore.FileName);

    [Fact]
    public void The_rejoin_record_round_trips_and_never_carries_a_password()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var record = RejoinRecord.From(Target(password: true), new[]
        {
            new RejoinRow { Id = A, Name = "Alpha", Package = "Owner-Alpha", Version = "1.2.0", Required = true, Result = "after-restart" },
        }, now);
        string json = record.ToJson();
        using (var doc = JsonDocument.Parse(json))
        {
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Equals("password", StringComparison.OrdinalIgnoreCase) || n.Equals("pass", StringComparison.OrdinalIgnoreCase)
                || n.Equals("pwd", StringComparison.OrdinalIgnoreCase) || n.Equals("connectPassword", StringComparison.OrdinalIgnoreCase));
            Assert.True(doc.RootElement.GetProperty("needsPassword").GetBoolean());
        }

        // The type has nowhere to keep one, and a password in a hand-edited file is ignored.
        Assert.DoesNotContain(typeof(RejoinRecord).GetProperties(), p => p.Name.Equals("Password", StringComparison.OrdinalIgnoreCase));
        Assert.True(RejoinRecord.TryParse(json.Replace("\"address\"", "\"password\": \"hunter2\", \"address\""), out var parsed));
        Assert.DoesNotContain("hunter2", parsed!.ToJson(), StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", parsed.Address);
        Assert.Equal(9976, parsed.Port);
        Assert.Equal("Lidgren", parsed.Protocol);
        Assert.Equal("after-restart", Assert.Single(parsed.Rows).Result);
        Assert.False(RejoinRecord.TryParse("{\"schema\":2}", out _));
        Assert.False(RejoinRecord.TryParse("not json", out _));
    }

    [Fact]
    public void The_rejoin_record_is_taken_once_and_an_old_or_used_or_broken_one_is_deleted()
    {
        string path = TempFile();
        try
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(RejoinPickup.None, RejoinStore.Take(path, now, out _));
            RejoinStore.Write(path, RejoinRecord.From(Target(), Array.Empty<RejoinRow>(), now));
            Assert.Equal(RejoinPickup.Rejoin, RejoinStore.Take(path, now.AddMinutes(9), out var record));
            Assert.Equal("127.0.0.1:9976", record!.ToTarget().Endpoint);
            Assert.True(RejoinStore.Peek(path)!.Used);
            // A second start (or a crash during the rejoin) never uses it again.
            Assert.Equal(RejoinPickup.AlreadyUsed, RejoinStore.Take(path, now.AddMinutes(9), out _));
            Assert.False(File.Exists(path));

            RejoinStore.Write(path, RejoinRecord.From(Target(), Array.Empty<RejoinRow>(), now));
            Assert.Equal(RejoinPickup.Expired, RejoinStore.Take(path, now.AddMinutes(11), out _));
            Assert.False(File.Exists(path));
            RejoinStore.Write(path, RejoinRecord.From(Target(), Array.Empty<RejoinRow>(), now.AddMinutes(30)));
            Assert.Equal(RejoinPickup.Expired, RejoinStore.Take(path, now, out _));

            File.WriteAllText(path, "{\"schema\":1,\"address\":\"\",\"port\":0}");
            Assert.Equal(RejoinPickup.Invalid, RejoinStore.Take(path, now, out _));
            Assert.False(File.Exists(path));
            var bad = Target();
            bad.Protocol = "SteamP2P";
            RejoinStore.Write(path, RejoinRecord.From(bad, Array.Empty<RejoinRow>(), now));
            Assert.Equal(RejoinPickup.Invalid, RejoinStore.Take(path, now, out _));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Only_the_two_direct_connect_transports_can_be_rejoined()
    {
        Assert.Equal("SteamIPv4", JoinTarget.CanonicalProtocol("steamipv4"));
        Assert.Equal("SteamIPv4", JoinTarget.CanonicalProtocol(""));
        Assert.Equal("Lidgren", JoinTarget.CanonicalProtocol("LIDGREN"));
        Assert.Equal("", JoinTarget.CanonicalProtocol("SteamP2P"));
        Assert.True(Target().CanReconnect);
        Assert.False(new JoinTarget { Address = "1.2.3.4", Port = 0 }.CanReconnect);
    }

    [Fact]
    public void The_summary_reads_ready_still_missing_and_server_must_update()
    {
        var record = RejoinRecord.From(Target(), new[]
        {
            new RejoinRow { Id = A, Name = "Alpha", Version = "1.2.0", Required = true, Result = "after-restart" },
            new RejoinRow { Id = B, Name = "Beta", Version = "1.0.0", Required = true, Result = "failed" },
            new RejoinRow { Id = "c", Name = "Gamma", Version = "1.0.0", Required = true, Result = "server-must-update" },
            new RejoinRow { Id = "maps", Name = "Maps", Version = "2.0.0", Required = false, Result = "after-restart" },
        }, DateTime.UtcNow);
        var s = RejoinSummary.From(record, id => id == A ? "1.2.0.0" : id == "maps" ? "2.0.0" : null);
        Assert.Equal(1, s.RequiredReady);
        Assert.Equal(3, s.RequiredTotal);
        Assert.Equal(1, s.RecommendedAdded);
        Assert.Equal(new[] { "Ready", "Still missing", "Server must update" }, s.Required.Select(kv => kv.Value));
        Assert.Equal("Required (1 of 3 ready)", JoinText.SummaryRequiredHeading(s.RequiredReady, s.RequiredTotal));
        Assert.Equal("After the restart: 1 of 3 required mods ready, 1 recommended added.", s.Line(afterRestart: true));
    }

    // ------------------------------------------------------------ the relaunch command

    [Fact]
    public void The_relaunch_command_waits_for_this_process_and_starts_the_game_once_through_steam()
    {
        string script = RelaunchCommand.Script(4242, 638_640_000_000_000_000, @"C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising.exe");
        Assert.Contains("Get-Process -Id 4242;", script, StringComparison.Ordinal);
        Assert.Contains("-638640000000000000)", script, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(180000)", script, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds 8;", script, StringComparison.Ordinal);
        Assert.Contains(@"$exe='C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising.exe';", script, StringComparison.Ordinal);
        // One start; a second ask only when no process with this executable appeared.
        Assert.Equal(2, CountOf(script, "Start-Process 'steam://run/1604030'"));
        Assert.Contains("if(Up){exit 3}", script, StringComparison.Ordinal);
        // 2026-10-09: the second ask also waits for Steam's own running/launching flag, after 90 s, so a slow Steam
        // start is never asked twice ("Game already running").
        Assert.Contains("(Get-Date).AddSeconds(90)", script, StringComparison.Ordinal);
        Assert.Contains(@"function Launching{(Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam\Apps\1604030' -Name Running).Running -eq 1};", script, StringComparison.Ordinal);
        Assert.Contains("if($exe.Length -gt 0 -and -not (Up) -and -not (Launching)){Start-Process", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", script, StringComparison.Ordinal);

        string args = RelaunchCommand.Arguments(script);
        Assert.StartsWith("-NoProfile -NonInteractive -WindowStyle Hidden -Command \"", args, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(args, "\""));
        Assert.EndsWith("\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("EncodedCommand", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutionPolicy", args, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"D:\Games\Bob's Stuff\VRising\VRising.exe", @"'D:\Games\Bob''s Stuff\VRising\VRising.exe'")]
    [InlineData(@"E:\Spiele & Mods\V Rising (copy)\VRising.exe", @"'E:\Spiele & Mods\V Rising (copy)\VRising.exe'")]
    [InlineData("C:\\Ga\u2019mes\\VRising.exe", "'C:\\Ga\u2019\u2019mes\\VRising.exe'")]
    [InlineData("C:\\a\u201Ab\u201Bc\u2018\\VRising.exe", "'C:\\a\u201A\u201Ab\u201B\u201Bc\u2018\u2018\\VRising.exe'")]
    [InlineData("C:\\a\r\nb\\VRising.exe", "'C:\\ab\\VRising.exe'")]
    [InlineData("", "''")]
    public void Odd_paths_are_single_quoted_with_quotes_doubled(string path, string quoted)
    {
        Assert.Equal(quoted, RelaunchCommand.Quote(path));
        string script = RelaunchCommand.Script(1, 1, path);
        Assert.Contains("$exe=" + quoted + ";", script, StringComparison.Ordinal);
        // Exactly the one pair of double quotes around the script: nothing in a path can break out of it.
        Assert.Equal(2, CountOf(RelaunchCommand.Arguments(script), "\""));
    }

    [Fact]
    public void The_relaunch_is_automatic_only_with_steam_running_its_handler_and_powershell()
    {
        Assert.Equal(RelaunchMode.Automatic, RelaunchCommand.Decide(true, true, true));
        Assert.Equal(RelaunchMode.Manual, RelaunchCommand.Decide(false, true, true));
        Assert.Equal(RelaunchMode.Manual, RelaunchCommand.Decide(true, false, true));
        Assert.Equal(RelaunchMode.Manual, RelaunchCommand.Decide(true, true, false));
        Assert.Equal(@"C:\Win Dows\System32\WindowsPowerShell\v1.0\powershell.exe", RelaunchCommand.PowerShellPath(@"C:\Win Dows\System32"));
        Assert.Throws<ArgumentException>(() => RelaunchCommand.Arguments("a\"b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelaunchCommand.Script(0, 0, "x"));
    }

    private static int CountOf(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += what.Length;
        }

        return n;
    }

    // ------------------------------------------------------------ the reconnect

    private sealed class Host : IJoinConnectHost
    {
        public bool World;
        public bool Menu;
        public bool Joined;
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => Joined && World;
        public void RequestLeave() => Calls.Add("leave");
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch:" + JoinTarget.CanonicalProtocol(target.Protocol) + "://" + target.Endpoint);
            return true;
        }
    }

    [Fact]
    public void Join_now_leaves_for_the_menu_then_connects_to_the_same_server_and_sees_it_through()
    {
        var host = new Host { World = true };
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(0.1);
        Assert.Equal(new[] { "leave" }, host.Calls);
        r.Tick(0.2);
        Assert.Single(host.Calls);
        host.World = false;
        host.Menu = true;
        r.Tick(1);
        Assert.Equal("launch:Lidgren://127.0.0.1:9976", host.Calls[1]);
        Assert.Equal("connecting", r.Phase);
        host.World = true;
        r.Tick(5);
        Assert.Equal("connecting", r.Phase);
        host.Joined = true;
        r.Tick(6);
        Assert.Equal("joined", r.Phase);
        Assert.Equal(100, r.Progress(6));
    }

    [Fact]
    public void The_disconnect_screen_goes_back_to_the_menu_and_a_stuck_or_refused_join_fails()
    {
        var host = new Host();
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(1);
        Assert.Empty(host.Calls);
        r.Tick(2.5);
        Assert.Equal(new[] { "menu" }, host.Calls);
        r.Tick(JoinReconnect.LeaveTimeoutSeconds + 1);
        Assert.Equal("failed", r.Phase);

        var host2 = new Host { Menu = true };
        var r2 = new JoinReconnect(Target(), host2, 0);
        r2.Tick(0);
        r2.Refused();
        Assert.Equal("failed", r2.Phase);
        Assert.Equal("the server refused the join", r2.Error);

        var host3 = new Host { Menu = true };
        var r3 = new JoinReconnect(Target(), host3, 0);
        r3.Tick(0);
        r3.Tick(JoinReconnect.ConnectTimeoutSeconds + 1);
        Assert.Equal("failed", r3.Phase);
    }

    [Fact]
    public void Cancel_before_the_connect_stops_it_and_after_the_connect_leaves_the_world_it_reaches()
    {
        var host = new Host();
        var r = new JoinReconnect(Target(), host, 0);
        r.Cancel();
        host.Menu = true;
        r.Tick(1);
        Assert.Empty(host.Calls);
        Assert.False(r.NeedsTicking);

        var host2 = new Host { Menu = true };
        var r2 = new JoinReconnect(Target(), host2, 0);
        r2.Tick(0);
        r2.Cancel();
        Assert.True(r2.NeedsTicking);
        host2.World = true;
        r2.Tick(3);
        Assert.Equal("leave", host2.Calls.Last());
        Assert.False(r2.NeedsTicking);
    }
}
