using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Bootstrap;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using ModRecord = Progenitor.Networking.Handshake.ModRecord;

namespace Progenitor.Tests;

/// <summary>
/// CLIENTPOLICY2 (docs/client-mod-policy.md, "The player's join screens"): the required client mod rows and their actions,
/// the "Not allowed on this server" rows, the summary line, the session's presses, the automatic pause (the plan and the
/// coordinator through a fake Bootstrap), the remembered policy, the signed run list, and the two fixes from the stage 1
/// live run. Game-free: nothing here has been observed in the game.
/// </summary>
[Collection("Handshake")]
public sealed class ClientPolicyStage2Tests : IDisposable
{
    private const string Robes = "test.robes.pack";
    private const string RobesPackage = "Owner-RobePack";
    private const string Extra = "extra.hot";
    private const string Plain = "plain.plugin";
    private readonly string _dir;

    public ClientPolicyStage2Tests()
    {
        ServerModPause.ResetForTests();
        ServerProfiles.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-policy2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ServerProfiles.TestDirectory = _dir;
    }

    public void Dispose()
    {
        ServerModPause.ResetForTests();
        ServerProfiles.TestDirectory = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    // ------------------------------------------------------------------ helpers

    private static ModRecord Must(string id, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    private static ClientModEntry Mod(string guid, string version = "1.0.0", string source = ClientModSources.Progenitor, string package = "", string? name = null) =>
        new(guid, name ?? guid.ToUpperInvariant(), version, source, package);

    private static ClientModRow Req(string id, string version, ClientModRule rule = ClientModRule.AtLeast, string package = RobesPackage,
        string reason = "Our events use these robes.", string name = "Robe Pack") =>
        new(id, package, version, rule, reason, name);

    private static JoinSection Section(ClientPolicyMode mode, IEnumerable<ClientModRow>? required = null, IEnumerable<ClientModRow>? approved = null,
        IEnumerable<string>? runs = null, IEnumerable<RecommendedMod>? recommended = null) =>
        new JoinSection("Welcome", null, recommended?.ToList() ?? new List<RecommendedMod>(), null)
            .WithPolicy(required?.ToList(), mode, approved?.ToList(), runs?.ToList());

    private static JoinPageModel Model(JoinSection section, IReadOnlyList<ClientModEntry>? mods, ModRecord[]? asked = null,
        ModRecord[]? provided = null, Func<string, bool>? pausable = null, Func<string, bool>? offAtRestart = null,
        IReadOnlyList<ClientModEntry>? pausedHere = null, IReadOnlyDictionary<string, Install.PackageVersions>? ts = null,
        Func<string, JoinOffCopy?>? off = null, bool refused = true) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = asked ?? Array.Empty<ModRecord>(),
            Provided = provided ?? Array.Empty<ModRecord>(),
            Section = section,
            Refused = refused,
            ClientMods = mods,
            Pausable = pausable,
            TurnsOffAtRestart = offAtRestart,
            PausedHere = pausedHere,
            ThunderstoreVersions = ts,
            OffCopyOf = off,
        });

    private static Dictionary<string, Install.PackageVersions> Listed(string package, params string[] versions) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [package] = new Install.PackageVersions(package, "", versions, null, null, local: true),
        };

    // ------------------------------------------------------------------ required client rows

    [Fact]
    public void A_missing_required_client_mod_with_a_package_offers_Install_with_the_reason_and_the_rule()
    {
        var model = Model(Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") }), new[] { Mod("progenitor") });
        var row = model.FindRequired(Robes)!;
        Assert.Equal(JoinRowOrigin.ClientPolicy, row.Origin);
        Assert.Equal("Robe Pack", row.Name);
        Assert.Equal(JoinRowStatus.Missing, row.Status);
        Assert.Equal(JoinRowAction.Install, row.Action);
        Assert.Equal("Our events use these robes.", row.Reason);
        Assert.Equal(ClientModRule.AtLeast, row.PolicyRule);
        Assert.Equal("1.2.0 or newer", JoinText.NeedsWords(row));
        Assert.Equal("Needs 1.2.0 or newer · You have nothing", JoinText.VersionLine(row));
        Assert.Equal("needs 1.2.0 or newer", JoinFlowLayout.CompactVersion(row));
        Assert.Equal("required-client", JoinText.OriginName(row.Origin));
        Assert.Equal(JoinRowSource.Thunderstore, row.Source);
        // "Or newer" asks Thunderstore for the named version, else the newest.
        Assert.Equal(NetworkVersionRule.None, row.VersionRule);
        Assert.Equal(new[] { Robes }, model.InstallTargets(null));
        var request = JoinFlowSession.RequestFor(row);
        Assert.Equal(Robes, request.ModId);
        Assert.Equal(RobesPackage, request.Package);
        Assert.Equal(1, model.ClientRequiredCount);
        Assert.False(model.AllRequiredReady);
    }

    [Fact]
    public void The_AtLeast_rule_passes_a_newer_copy_and_asks_for_an_update_of_an_older_one()
    {
        var section = Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") });
        var newer = Model(section, new[] { Mod(Robes, "1.3.0") }).FindRequired(Robes)!;
        Assert.Equal(JoinRowStatus.Ready, newer.Status);
        Assert.Equal(JoinRowAction.Ready, newer.Action);
        var older = Model(section, new[] { Mod(Robes, "1.1.0") }).FindRequired(Robes)!;
        Assert.Equal(JoinRowStatus.Older, older.Status);
        Assert.Equal(JoinRowAction.Update, older.Action);
        Assert.Equal("Update needed", JoinFlowLayout.StatusWord(older));
        Assert.Equal("you have 1.1.0, needs 1.2.0 or newer", JoinFlowLayout.CompactVersion(older));
    }

    [Fact]
    public void An_exact_rule_refuses_a_newer_copy_and_offers_the_exact_version_when_Thunderstore_serves_it()
    {
        var section = Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0", ClientModRule.Exact) });
        var unknown = Model(section, new[] { Mod(Robes, "1.3.0") }).FindRequired(Robes)!;
        Assert.Equal(JoinRowStatus.Newer, unknown.Status);
        Assert.Equal("Newer than allowed", JoinFlowLayout.StatusWord(unknown));
        Assert.Equal(JoinRowAction.TellAdmin, unknown.Action);
        Assert.Contains("Could the rule allow newer versions?", JoinText.TellAdminMessage(unknown, "Night Garden"));
        var listed = Model(section, new[] { Mod(Robes, "1.3.0") }, ts: Listed(RobesPackage, "1.3.0", "1.2.0")).FindRequired(Robes)!;
        Assert.Equal(JoinRowAction.UseVersion, listed.Action);
        Assert.Equal("Use version 1.2.0", JoinText.ActionCaption(listed));
        Assert.Equal(NetworkVersionRule.Exact, listed.VersionRule);
    }

    [Fact]
    public void A_required_client_mod_without_a_package_asks_the_admin()
    {
        var model = Model(Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0", package: "") }), Array.Empty<ClientModEntry>());
        var row = model.FindRequired(Robes)!;
        Assert.Equal(JoinRowAction.AskAdmin, row.Action);
        Assert.Equal("Robe Pack: this server didn't say where to get it. Ask its admins.", JoinText.CannotFixFooter(model));
        Assert.Contains("1.2.0 or newer", JoinText.AskAdminMessage(row, "Night Garden"));
    }

    [Fact]
    public void A_row_named_by_its_package_alone_is_found_through_the_install_ledger_and_installs_without_a_plugin_check()
    {
        var row = new ClientModRow("", RobesPackage, "1.0.0", ClientModRule.AtLeast, "", "");
        var section = Section(ClientPolicyMode.Any, new[] { row });
        var ready = Model(section, new[] { Mod("someone.robes", "1.0.4", package: RobesPackage) }).FindRequired(RobesPackage)!;
        Assert.Equal(JoinRowStatus.Ready, ready.Status);
        var missing = Model(section, Array.Empty<ClientModEntry>()).FindRequired(RobesPackage)!;
        Assert.Equal(JoinRowAction.Install, missing.Action);
        Assert.Equal("", missing.ModId);
        Assert.Equal("", JoinFlowSession.RequestFor(missing).ModId);
        Assert.Equal("RobePack", missing.Name);
    }

    [Fact]
    public void A_copy_the_player_turned_off_that_passes_the_rule_is_turned_on_not_downloaded()
    {
        Func<string, JoinOffCopy?> off = id => id == Robes ? new JoinOffCopy { Version = "1.4.0", AppliesNow = true, TurnsOnWithInstall = true } : null;
        var model = Model(Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") }), Array.Empty<ClientModEntry>(), off: off);
        var row = model.FindRequired(Robes)!;
        Assert.Equal(JoinRowStatus.TurnedOff, row.Status);
        Assert.Equal(JoinRowAction.TurnOn, row.Action);
        Assert.Equal(new[] { "applies now" }, JoinText.TagWords(row.Tags));
        Assert.Empty(model.InstallTargets(null));
        Assert.Equal(Robes, JoinTurnOnTarget.From(row).Id);
    }

    [Fact]
    public void A_required_client_row_installs_from_Thunderstore_and_a_mod_the_server_already_requires_keeps_its_row()
    {
        var shared = new ManifestMod(Robes, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false, null, RobesPackage,
            new string('c', 64), 1234);
        var catalog = new ManifestBody(1, "Night Garden", "2026-10-04T12:00:00Z", new ManifestOperator("Ada", "", "a1b2c3d4e5f67890"), null, new[] { shared });
        var model = JoinPageModel.Build(new JoinFacts
        {
            Section = Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") }),
            Catalog = catalog,
            Refused = true,
            ClientMods = Array.Empty<ClientModEntry>(),
        });
        Assert.Equal(JoinRowAction.Install, model.FindRequired(Robes)!.Action);

        // The server's own request asks for the same mod: one row, the server's.
        var both = Model(Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") }), Array.Empty<ClientModEntry>(), asked: new[] { Must(Robes, "1.2.0") });
        Assert.Single(both.Required);
        Assert.Equal(JoinRowOrigin.ServerRequires, both.Required[0].Origin);
    }

    [Fact]
    public void Without_this_clients_mod_list_the_loaded_version_decides()
    {
        var model = JoinPageModel.Build(new JoinFacts
        {
            Section = Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.2.0") }),
            LoadedVersionOf = id => id == Robes ? "1.2.5" : null,
        });
        Assert.True(model.FindRequired(Robes)!.Ready);
    }

    // ------------------------------------------------------------------ not allowed on this server

    [Fact]
    public void Only_approved_lists_each_unapproved_loaded_mod_with_the_fix_this_client_can_make()
    {
        var section = Section(ClientPolicyMode.Approved,
            required: new[] { Req(Robes, "1.0.0") },
            approved: new[] { new ClientModRow("ok.mod", "", "", ClientModRule.Any), new ClientModRow("pinned.mod", "", "2.0.0", ClientModRule.Exact) },
            runs: new[] { "server.runs.it" },
            recommended: new[] { new RecommendedMod("rec.mod", "", "", "") });
        var mods = new[]
        {
            Mod("progenitor"), Mod("BepInEx.Something"), Mod(Robes, "1.0.0"), Mod("ok.mod"), Mod("server.runs.it"),
            Mod("rec.mod"), Mod("asked.mod"), Mod("pinned.mod", "2.1.0"), Mod(Extra), Mod(Plain, source: ClientModSources.Chainloader),
            Mod("off.later", source: ClientModSources.Chainloader),
        };
        var model = Model(section, mods, asked: new[] { Must("asked.mod", "1.0.0") },
            pausable: id => id == Extra || id == "pinned.mod", offAtRestart: id => id == "off.later",
            pausedHere: new[] { Mod("was.paused") });
        var ids = model.NotAllowed.Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "pinned.mod", Extra, Plain, "off.later", "was.paused" }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(JoinNotAllowedAction.Pause, model.FindNotAllowed(Extra)!.Action);
        Assert.Equal(ClientPolicyView.PauseButton, ClientPolicyView.ActionCaption(model.FindNotAllowed(Extra)!));
        var plain = model.FindNotAllowed(Plain)!;
        Assert.Equal(JoinNotAllowedAction.TurnOffRestart, plain.Action);
        Assert.Equal(ClientPolicyView.TurnOffRestartButton, ClientPolicyView.ActionCaption(plain));
        Assert.Equal("Not allowed · 1.0.0 · can't be paused", ClientPolicyView.RowLine(plain));
        Assert.DoesNotContain(ClientPolicyView.StaysOffLine, ClientPolicyView.RowLine(plain));
        Assert.Equal(JoinNotAllowedState.TurnsOffAtRestart, model.FindNotAllowed("off.later")!.State);
        Assert.False(model.FindNotAllowed("off.later")!.Unresolved);
        Assert.Equal(JoinNotAllowedState.Paused, model.FindNotAllowed("was.paused")!.State);
        Assert.Equal("Paused for this server", ClientPolicyView.RowLead(model.FindNotAllowed("was.paused")!));
        Assert.Equal(3, model.NotAllowedUnresolved);
        Assert.Equal(model.Required.Count - model.RequiredReady + 3, model.NeedAttention);
    }

    /// <summary>
    /// Live 2026-10-05 (3840x1080): "Not allowed · It stays off until you…", "Progenitor API Validation …" and
    /// "Paused for this server · 0.1.0 · bac…" were cut. Every drawn part of a not-allowed row now fits its width, the long
    /// sentence is the confirmation's, and the sub-heading has the section headings' room above it.
    /// </summary>
    [Fact]
    public void Nothing_a_not_allowed_row_draws_exceeds_its_fitted_width()
    {
        var rows = new[]
        {
            new JoinNotAllowedRow { Id = "a", Name = "RetainedPassword", Version = "1.0.2", Source = ClientModSources.Chainloader,
                State = JoinNotAllowedState.Loaded, Action = JoinNotAllowedAction.TurnOffRestart },
            new JoinNotAllowedRow { Id = "b", Name = "Progenitor API Validation (UI assertions and more)", Version = "0.1.0",
                Pausable = true, State = JoinNotAllowedState.Paused },
            new JoinNotAllowedRow { Id = "c", Name = "A very long hot-loadable mod name that goes on and on", Version = "2026.10.68.1",
                Pausable = true, State = JoinNotAllowedState.Loaded, Action = JoinNotAllowedAction.Pause },
            new JoinNotAllowedRow { Id = "d", Name = "Off later", Version = "3.2.1", State = JoinNotAllowedState.TurnsOffAtRestart },
        };
        foreach (float width in new[] { 520f, 600f, JoinFlowLayout.SectionRowWidth(DialogLayout.ContentRowWidth()), 900f })
            foreach (var row in rows)
            {
                Assert.True(JoinFlowLayout.NotAllowedFits(row, width), row.Name + " at " + width);
                JoinFlowLayout.NotAllowedLine(row, JoinFlowLayout.NotAllowedTextWidth(row, width), out _, out string lead, out string note);
                Assert.DoesNotContain(ClientPolicyView.StaysOffLine, lead + note);
                var spec = JoinFlowLayout.NotAllowed(row, width);
                Assert.DoesNotContain(spec.Cells, c => (c.Text ?? "").Contains("It stays off", StringComparison.Ordinal));
            }

        // At the popup's width the detail is kept before the version is.
        float popup = JoinFlowLayout.SectionRowWidth(DialogLayout.ContentRowWidth());
        JoinFlowLayout.NotAllowedLine(rows[0], JoinFlowLayout.NotAllowedTextWidth(rows[0], popup), out _, out string l0, out string n0);
        Assert.Equal("Not allowed", l0);
        Assert.EndsWith("can't be paused", n0);
        JoinFlowLayout.NotAllowedLine(rows[1], JoinFlowLayout.NotAllowedTextWidth(rows[1], popup), out _, out string l1, out string n1);
        Assert.Equal("Paused for this server", l1);
        Assert.EndsWith("back when you leave", n1);
        // A wide row keeps every part.
        JoinFlowLayout.NotAllowedLine(rows[0], 600f, out _, out _, out string wide);
        Assert.Equal("1.0.2 · can't be paused", wide);
        // A resolved row's action cell is the narrow one, so its name gets the room.
        Assert.True(JoinFlowLayout.NotAllowedTextWidth(rows[1], popup) > JoinFlowLayout.NotAllowedTextWidth(rows[2], popup));
        // The sub-heading: a gap row, then a heading as tall as a section heading, with the count.
        Assert.Equal(JoinFlowLayout.NotAllowedGap, JoinFlowLayout.NotAllowedSpacer().Height);
        var head = JoinFlowLayout.NotAllowedHeading(2);
        Assert.Equal(JoinFlowLayout.SectionHeading("x", "t", "n", CellSection.Red).Height, head.Height);
        Assert.Equal("2 mods", head.Cells.Single(c => c.Key == JoinFlowLayout.CellCount).Text);
    }

    [Fact]
    public void Any_mods_lists_nothing_as_not_allowed()
    {
        var model = Model(Section(ClientPolicyMode.Any, new[] { Req(Robes, "1.0.0") }), new[] { Mod(Extra), Mod(Robes) }, pausable: _ => true);
        Assert.Empty(model.NotAllowed);
        Assert.True(model.AllRequiredReady);
    }

    // ------------------------------------------------------------------ summary

    [Fact]
    public void The_summary_counts_client_rows_and_mods_not_allowed_here()
    {
        var asked = new[] { Must("a", "1"), Must("b", "1"), Must("c", "1"), Must("d", "1") };
        var provided = asked.ToArray();
        var section = Section(ClientPolicyMode.Approved,
            required: new[] { Req(Robes, "1.0.0"), Req("cape.pack", "1.0.0", package: "Owner-Cape", name: "Cape Pack") },
            recommended: new[] { new RecommendedMod("r1", "", "", ""), new RecommendedMod("r2", "", "", ""), new RecommendedMod("r3", "", "", "") });
        var mods = new[] { Mod("a", "1"), Mod("b", "1"), Mod("c", "1"), Mod("d", "1"), Mod(Robes), Mod("cape.pack"), Mod(Extra) };
        var model = Model(section, mods, asked, provided, pausable: id => id == Extra);
        Assert.Equal("6 required mods (2 client), 3 recommended. 1 mod isn't allowed here.", JoinText.Summary(model));

        var attention = Model(Section(ClientPolicyMode.Approved, required: new[] { Req(Robes, "2.0.0") }),
            new[] { Mod(Robes, "1.0.0"), Mod(Extra), Mod(Plain) }, pausable: id => id == Extra);
        Assert.Equal("1 required mod (1 client). 1 needs attention. 2 mods aren't allowed here.", JoinText.Summary(attention));

        var none = Model(Section(ClientPolicyMode.Approved), new[] { Mod(Extra) }, pausable: _ => true);
        Assert.Equal("0 required mods. 1 mod isn't allowed here.", JoinText.Summary(none));

        // Without a policy the stage 1 wording stands.
        var plain = Model(new JoinSection("", null, new[] { new RecommendedMod("r1", "", "", "") }, null), null,
            new[] { Must("a", "1"), Must("b", "1") }, new[] { Must("a", "1"), Must("b", "1") });
        Assert.Equal("2 required mods, 1 recommended. All ready.", JoinText.Summary(plain));
    }

    // ------------------------------------------------------------------ the session's presses

    private sealed class Effects : IJoinFlowEffects
    {
        public readonly List<string> Calls = new();
        public JoinResultOffer? Offer;

        public bool CopyToClipboard(string text) => true;
        public void Leave() { }
        public void Closed(bool completed) { }
        public void MarkSeen() { }
        public void PageShown(JoinPage page) { }

        public string PauseForServer(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
        {
            Calls.Add("pause:" + string.Join(",", rows.Select(r => r.Id)));
            offer = Offer;
            return "paused";
        }

        public string TurnOffForRestart(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
        {
            Calls.Add("off:" + string.Join(",", rows.Select(r => r.Id)));
            offer = Offer;
            return "turned off";
        }
    }

    [Fact]
    public void A_not_allowed_rows_press_reaches_the_pause_or_the_switch_and_the_result_page_follows()
    {
        var model = Model(Section(ClientPolicyMode.Approved), new[] { Mod(Extra), Mod(Plain, source: ClientModSources.Chainloader) },
            pausable: id => id == Extra);
        var effects = new Effects();
        var s = new JoinFlowSession(JoinMode.Refused, model, effects);
        if (s.Page == JoinPage.Message) s.Continue();
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal("pause", s.NotAllowedAction(Extra));
        Assert.Equal("paused", s.Notice);
        effects.Offer = new JoinResultOffer { Kind = JoinAfterInstall.RestartNeeded, CanRejoin = true, Summary = "restart" };
        Assert.Equal("turn-off-restart", s.NotAllowedAction(Plain));
        Assert.Equal(JoinPage.Result, s.Page);
        Assert.Equal(new[] { "pause:" + Extra, "off:" + Plain }, effects.Calls);
        Assert.Equal("not on the mods page", s.NotAllowedAction(Extra));
    }

    [Fact]
    public void A_resolved_row_does_nothing_and_an_unknown_row_says_so()
    {
        var model = Model(Section(ClientPolicyMode.Approved), Array.Empty<ClientModEntry>(), pausedHere: new[] { Mod(Extra) });
        Assert.Equal(0, model.NeedAttention);
        var s = new JoinFlowSession(JoinMode.InWorld, model, new Effects());
        if (s.Page == JoinPage.Message) s.ViewMods();
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal("done", s.NotAllowedAction(Extra));
        Assert.Equal("no not-allowed row nope", s.NotAllowedAction("nope"));
    }

    [Fact]
    public void After_an_install_a_mod_only_a_restart_removes_keeps_the_join_incomplete_until_it_is_switched_off()
    {
        var rows = new[]
        {
            new JoinInstallRowState(new JoinInstallRequest(Robes, "Robe Pack", RobesPackage, "1.2.0", JoinInstallKind.Install, required: true, modId: Robes))
            {
                Phase = JoinInstallPhase.Loaded,
            },
        };
        var section = Section(ClientPolicyMode.Approved, required: new[] { Req(Robes, "1.2.0") });
        var blocked = Model(section, new[] { Mod(Robes, "1.2.0"), Mod(Plain, source: ClientModSources.Chainloader) });
        Assert.Equal(JoinAfterInstall.Incomplete, JoinAfterInstallPolicy.Decide(blocked, rows));
        var later = Model(section, new[] { Mod(Robes, "1.2.0"), Mod(Plain, source: ClientModSources.Chainloader) }, offAtRestart: id => id == Plain);
        Assert.Equal(JoinAfterInstall.RestartNeeded, JoinAfterInstallPolicy.Decide(later, rows));
        var pausable = Model(section, new[] { Mod(Robes, "1.2.0"), Mod(Extra) }, pausable: id => id == Extra);
        Assert.Equal(JoinAfterInstall.JoinNow, JoinAfterInstallPolicy.Decide(pausable, rows));
    }

    // ------------------------------------------------------------------ the automatic pause: the plan

    private static Func<IReadOnlyList<string>, (IReadOnlyList<string>, string)> Identity => g => (g, "");

    [Fact]
    public void The_plan_pauses_the_pausable_mods_the_policy_does_not_allow_and_lists_the_rest()
    {
        var section = Section(ClientPolicyMode.Approved, approved: new[] { new ClientModRow("ok.mod", "", "", ClientModRule.Any) });
        var loaded = new[] { Mod("ok.mod"), Mod(Extra), Mod(Plain, source: ClientModSources.Chainloader) };
        var plan = ClientPolicyView.PlanPause(section, Array.Empty<ModRecord>(), loaded, new[] { "ok.mod", Extra }, Identity, null, null, autoPause: true);
        Assert.Equal(new[] { Extra }, plan.Pause);
        Assert.Equal(new[] { Plain }, plan.RestartOnly);
        Assert.Equal(new[] { Extra, Plain }.OrderBy(x => x), plan.NotAllowed.OrderBy(x => x));

        Assert.Empty(ClientPolicyView.PlanPause(Section(ClientPolicyMode.Any), null, loaded, new[] { Extra }, Identity, null, null, true).Pause);
        Assert.Empty(ClientPolicyView.PlanPause(null, null, loaded, new[] { Extra }, Identity, null, null, true).Pause);
        var off = ClientPolicyView.PlanPause(section, null, loaded, new[] { Extra }, Identity, null, null, autoPause: false);
        Assert.Empty(off.Pause);
        Assert.Equal(ClientPolicyView.WhyAutoPauseOff, off.Why);
        var none = ClientPolicyView.PlanPause(section, null, new[] { Mod(Plain) }, Array.Empty<string>(), Identity, null, null, true);
        Assert.Equal(ClientPolicyView.WhyNonePausable, none.Why);
    }

    [Fact]
    public void The_plan_takes_nothing_when_the_closure_would_take_a_mod_the_server_asks_about_or_requires()
    {
        var section = Section(ClientPolicyMode.Approved, required: new[] { Req(Robes, "1.0.0") });
        var loaded = new[] { Mod(Extra), Mod(Robes) };
        var withAsked = ClientPolicyView.PlanPause(section, new[] { Must("asked.mod", "1.0.0") }, loaded, new[] { Extra },
            g => (g.Concat(new[] { "asked.mod" }).ToList(), ""), null, null, true);
        Assert.Empty(withAsked.Pause);
        Assert.Equal(ClientPolicyView.WhyAsked, withAsked.Why);
        // A row the server asks about that the closure's plugin provides.
        var rowOwner = ClientPolicyView.PlanPause(section, new[] { Must("asked.row", "1.0.0") }, loaded, new[] { Extra }, Identity,
            new[] { Must("asked.row", "1.0.0") }, new Dictionary<string, string> { ["asked.row"] = Extra }, true);
        Assert.Equal(ClientPolicyView.WhyAsked, rowOwner.Why);
        var withRequired = ClientPolicyView.PlanPause(section, null, loaded, new[] { Extra }, g => (g.Concat(new[] { Robes }).ToList(), ""), null, null, true);
        Assert.Equal(ClientPolicyView.WhyRequired, withRequired.Why);
        var refused = ClientPolicyView.PlanPause(section, null, loaded, new[] { Extra }, _ => (Array.Empty<string>(), "it has dependents"), null, null, true);
        Assert.Equal("it has dependents", refused.Why);
    }

    // ------------------------------------------------------------------ the automatic pause: the coordinator

    private sealed class Backend : IServerModPauseBackend
    {
        internal readonly Dictionary<string, bool> Loaded = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Unsafe = new(StringComparer.OrdinalIgnoreCase);
        internal readonly PluginSuspendTable Table = new();
        internal readonly List<string> Calls = new();
        public string PendingRequest { get; set; } = "";
        public string ResumeReports { get; set; } = "";
        public bool Supported => true;
        public string UnsupportedReason => "";
        public bool? HostsPrivateGame() => false;
        public string ConnectionIdentity() => "203.0.113.5:9876@1";
        public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids) => Table.CancelResume(guids);

        public IReadOnlyList<string> Suspendable() => Loaded.Where(p => p.Value && !Unsafe.Contains(p.Key)).Select(p => p.Key).ToList();

        public IReadOnlyList<PausedPlugin> Suspended()
        {
            var list = new List<PausedPlugin>();
            foreach (var row in Table.Rows())
                if (PausedPlugin.TryParse(row, out var parsed)) list.Add(parsed);
            return list;
        }

        public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal)
        {
            refusal = "";
            return guids.Where(g => Suspendable().Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report)
        {
            report = "";
            var done = new List<string>();
            foreach (var g in guids)
            {
                if (!Loaded.TryGetValue(g, out bool on) || !on) continue;
                Loaded[g] = false;
                Table.Add(new SuspendedUnit(g + ".dll", g + ".dll", "", DateTime.UtcNow,
                    new List<SuspendedPlugin> { new(g, g.ToUpperInvariant(), "1.0.0", new List<string>(), new List<string>()) }, Array.Empty<string>()));
                done.Add(g);
            }

            Calls.Add("suspend:" + string.Join(",", done));
            return done;
        }

        public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report)
        {
            report = "";
            return Array.Empty<string>();
        }

        internal IReadOnlyList<ClientModEntry> Mods() =>
            Loaded.Where(p => p.Value).Select(p => Mod(p.Key)).Concat(new[] { Mod(Plain, source: ClientModSources.Chainloader) }).ToList();
    }

    private (Backend Backend, List<string> Notices, List<string> Replies) Coordinator(bool serverAsks, JoinSection? remembered)
    {
        var backend = new Backend();
        backend.Loaded[Extra] = true;
        backend.Loaded["ok.mod"] = true;
        var notices = new List<string>();
        var replies = new List<string>();
        ServerModPause.Test = new ServerModPause.TestSeams
        {
            Backend = backend,
            Post = a => a(),
            PostDelayed = (s, a) => a(),
            Host = () => "203.0.113.5:9876",
            Clock = () => 0,
            Provided = () => Array.Empty<ModRecord>(),
            Owners = () => new Dictionary<string, string>(),
            Asked = () => Array.Empty<ModRecord>(),
            ReAnswer = () => true,
            Watch = new NoProgenitorWatch(),
            ClientMods = backend.Mods,
            ServerAsksReport = () => serverAsks,
            RememberedPolicy = _ => remembered == null ? "" : ClientPolicyView.Remembered(remembered),
        };
        ServerModPause.NoticeRequested += (title, text) => notices.Add(text);
        return (backend, notices, replies);
    }

    [Fact]
    public void Before_the_reply_the_automatic_pause_takes_what_the_remembered_policy_does_not_allow()
    {
        var section = Section(ClientPolicyMode.Approved, approved: new[] { new ClientModRow("ok.mod", "", "", ClientModRule.Any) });
        var (backend, notices, replies) = Coordinator(serverAsks: true, section);
        ServerModPause.OnRequest(new List<ModRecord>(), false, () =>
        {
            replies.Add(string.Join(",", backend.Mods().Select(m => m.Guid)));
            return true;
        });
        Assert.Equal(new[] { "suspend:" + Extra }, backend.Calls);
        // The reply (and the report after it) no longer lists the paused mod; the plain plugin stays (refused for it).
        Assert.Single(replies);
        Assert.DoesNotContain(Extra, replies[0].Split(','));
        Assert.Contains(Plain, replies[0].Split(','));
        Assert.Equal(new[] { "Paused 1 mod for this server: EXTRA.HOT (Not allowed on this server). They come back when you leave." }, notices);
        var d = ServerModPause.DescribePolicyPause();
        Assert.Equal(true, d["remembered"]);
        var last = (Dictionary<string, object?>)d["lastPause"]!;
        Assert.Equal(new List<string> { Extra }, last["paused"]);
        Assert.Equal(new List<string> { Plain }, last["restartOnly"]);
    }

    [Fact]
    public void Nothing_is_paused_when_the_server_did_not_ask_or_no_policy_is_remembered()
    {
        var section = Section(ClientPolicyMode.Approved);
        var (backend, notices, replies) = Coordinator(serverAsks: false, section);
        ServerModPause.OnRequest(new List<ModRecord>(), false, () => { replies.Add("reply"); return true; });
        Assert.Empty(backend.Calls);
        Assert.Single(replies);
        Assert.Empty(notices);

        ServerModPause.ResetForTests();
        var (backend2, notices2, replies2) = Coordinator(serverAsks: true, null);
        ServerModPause.OnRequest(new List<ModRecord>(), false, () => { replies2.Add("reply"); return true; });
        Assert.Empty(backend2.Calls);
        Assert.Single(replies2);
        Assert.Empty(notices2);
    }

    [Fact]
    public void Pause_for_this_server_pauses_a_hot_mod_and_refuses_one_loaded_at_start()
    {
        var (backend, _, _) = Coordinator(serverAsks: true, Section(ClientPolicyMode.Approved));
        var ok = ServerModPause.PauseForPolicy(new[] { Extra });
        Assert.True(ok.Ok);
        Assert.Equal(new[] { Extra }, ok.Taken);
        Assert.Equal(new[] { "EXTRA.HOT" }, ok.Names);
        var no = ServerModPause.PauseForPolicy(new[] { Plain });
        Assert.False(no.Ok);
        Assert.Contains("cannot be paused", no.Message);
        Assert.Equal(new[] { "suspend:" + Extra }, backend.Calls);
    }

    // ------------------------------------------------------------------ the remembered policy and the signed run list

    [Fact]
    public void A_remembered_policy_reads_back_as_the_section_it_came_from()
    {
        var section = Section(ClientPolicyMode.Approved,
            required: new[] { Req(Robes, "1.2.0", ClientModRule.Minor) },
            approved: new[] { new ClientModRow("ok.mod", "Owner-Ok", "2.0.0", ClientModRule.Exact, "", "Ok Mod") },
            runs: new[] { "Server.Mod", "a.mod" },
            recommended: new[] { new RecommendedMod("rec.mod", "Owner-Rec", "1.0", "why") });
        var back = ClientPolicyView.FromRemembered(ClientPolicyView.Remembered(section))!;
        Assert.Equal(ClientPolicyMode.Approved, back.PolicyMode);
        Assert.Equal(ClientModRule.Minor, back.RequiredClient.Single().Rule);
        Assert.Equal("Robe Pack", back.RequiredClient.Single().Name);
        Assert.Equal("Ok Mod", back.Approved.Single().Name);
        Assert.Equal(new[] { "a.mod", "server.mod" }, back.ServerRuns);
        Assert.Equal("rec.mod", back.Recommended.Single().Id);
        Assert.Equal("", ClientPolicyView.Remembered(new JoinSection("hi", null, null, null)));
        Assert.Null(ClientPolicyView.FromRemembered("not json"));
    }

    [Fact]
    public void The_server_profile_remembers_a_policy_forgets_it_when_the_server_drops_it_and_keeps_at_most_32()
    {
        string policy = ClientPolicyView.Remembered(Section(ClientPolicyMode.Approved));
        ServerProfiles.NotePolicy("203.0.113.5:9876", policy, DateTime.UtcNow);
        Assert.Equal(policy, ServerProfiles.PolicyOf("203.0.113.5:9876"));
        ServerProfiles.NotePolicy("203.0.113.5:9876", "", DateTime.UtcNow);
        Assert.Equal("", ServerProfiles.PolicyOf("203.0.113.5:9876"));
        // A server never seen with a policy writes nothing.
        ServerProfiles.NotePolicy("198.51.100.1:1", "", DateTime.UtcNow);
        Assert.DoesNotContain("198.51.100.1", File.ReadAllText(ServerProfiles.FilePath));
        var t0 = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < ServerProfiles.MaxPolicies + 2; i++)
            ServerProfiles.NotePolicy("10.0.0." + i + ":9876", policy, t0.AddMinutes(i));
        Assert.Equal("", ServerProfiles.PolicyOf("10.0.0.0:9876"));
        Assert.Equal("", ServerProfiles.PolicyOf("10.0.0.1:9876"));
        Assert.Equal(policy, ServerProfiles.PolicyOf("10.0.0.2:9876"));
        Assert.Equal(policy, ServerProfiles.PolicyOf("10.0.0." + (ServerProfiles.MaxPolicies + 1) + ":9876"));
    }

    [Fact]
    public void The_run_list_is_signed_only_under_Only_approved_and_reads_back()
    {
        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved };
        var section = JoinSectionJson.WithClientPolicy(new JoinSection("m", null, null, null), policy, new[] { "Z.mod", "a.mod", "bad id!", "a.mod" });
        Assert.Equal(new[] { "a.mod", "z.mod" }, section.ServerRuns);
        string canonical = JoinSectionJson.Canonical(section);
        Assert.Contains("\"runs\":[\"a.mod\",\"z.mod\"]", canonical);
        using (var doc = JsonDocument.Parse(canonical))
        {
            Assert.True(JoinSectionJson.TryRead(doc.RootElement, out var read, out var error), error);
            Assert.Equal(new[] { "a.mod", "z.mod" }, read!.ServerRuns);
            Assert.Equal(canonical, JoinSectionJson.Canonical(read));
        }

        var any = JoinSectionJson.WithClientPolicy(new JoinSection("m", null, null, null), new ClientPolicyConfig(), new[] { "a.mod" });
        Assert.Empty(any.ServerRuns);
        Assert.DoesNotContain("\"runs\"", JoinSectionJson.Canonical(any));

        var tooMany = "{\"schema\":1,\"message\":\"\",\"runs\":[" + string.Join(",", Enumerable.Range(0, JoinSectionLimits.MaxServerRuns + 1).Select(i => "\"m" + i + "\"")) + "]}";
        using var big = JsonDocument.Parse(tooMany);
        Assert.False(JoinSectionJson.TryRead(big.RootElement, out _, out var why));
        Assert.Equal("join runs are over their caps", why);
    }

    [Fact]
    public void A_mod_on_the_signed_run_list_is_allowed_at_any_version_and_an_asked_row_keeps_its_rule()
    {
        var section = Section(ClientPolicyMode.Approved, runs: new[] { "srv.mod" });
        var facts = ClientPolicyView.FactsOf(section, new[] { Must("asked.mod", "1.0.0") });
        var policy = ClientPolicyView.PolicyOf(section)!;
        Assert.True(ClientModJudge.Allowed(policy, facts, Mod("srv.mod", "9.9.9")));
        Assert.True(ClientModJudge.Allowed(policy, facts, Mod("asked.mod", "1.0.0")));
        Assert.False(ClientModJudge.Allowed(policy, facts, Mod("asked.mod", "2.0.0")));
    }

    // ------------------------------------------------------------------ fixes from the stage 1 live run

    [Fact]
    public void A_brand_new_player_is_named_by_platform_id_in_the_server_log()
    {
        Assert.Equal("TestPlayer", Handshake.Compare.PlayerLabel("TestPlayer", 76561198000000001));
        Assert.Equal("a new player (76561198000000001)", Handshake.Compare.PlayerLabel("", 76561198000000001));
        Assert.Equal("a new player (76561198000000001)", Handshake.Compare.PlayerLabel("  ", 76561198000000001));
        Assert.Equal("a new player", Handshake.Compare.PlayerLabel(null, 0));
    }

    [Fact]
    public void The_refused_join_alert_keeps_the_admins_name_for_a_required_client_mod()
    {
        var policy = new ClientPolicyConfig();
        policy.Required.Add(Req(Robes, "1.2.0"));
        policy.Mode = ClientPolicyMode.Approved;
        var report = new ClientModReport();
        report.Entries.Add(Mod("cheat.mod", name: "A Sentence Of Text"));
        var verdict = ClientModJudge.Judge(policy, report, new ClientModServerFacts());
        var names = ClientModJudge.RecordNames(verdict);
        Assert.Equal("Robe Pack", names[Robes]);
        Assert.False(names.ContainsKey("cheat.mod"));

        var log = new RefusedJoinLog();
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        log.Record(ClientModJudge.RecordRows(verdict), now, names);
        var (_, rows) = log.Aggregate(now, 5, _ => null);
        Assert.Contains(rows, r => r.Text == "Robe Pack: players don't have it (required by you) (1 join)");
        Assert.Contains(rows, r => r.Text == "cheat.mod: not approved (1 join)");
        // The name survives the file.
        var back = RefusedJoinLog.Parse(log.ToJson(), now);
        var (_, again) = back.Aggregate(now, 5, _ => null);
        Assert.Contains(again, r => r.Text == "Robe Pack: players don't have it (required by you) (1 join)");
        // A live lookup still wins (a renamed row).
        var (_, renamed) = back.Aggregate(now, 5, id => id == Robes ? "Robes v2" : null);
        Assert.Contains(renamed, r => r.Text.StartsWith("Robes v2:", StringComparison.Ordinal));
    }
}
