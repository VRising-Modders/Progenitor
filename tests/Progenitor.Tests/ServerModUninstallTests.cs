using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.PluginActions;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SRVUNINST1 (live 2026-10-07): the Mods page's Uninstall per side, the server's identification of
/// its own plugins, and the Install tab's refused-uninstall and reload-folder cells. Game-free; the
/// service paths that touch disk are in <see cref="RemoteInstallServiceTests"/>. Not live-verified.
/// </summary>
public sealed class ServerModUninstallTests
{
    private const string Ecf = "gg.dyno.ExampleCommandFramework";
    private static readonly RemoteInstallActor Admin = new("Ada", 76561198000000001UL, isAdmin: true);

    private static readonly ServerRoute Remote = new(ServerRouteKind.Remote, ServerRouteBlock.None);
    private static readonly ServerIdentity TestServer = ServerIdentity.Connected("Forms Test", "127.0.0.1", 9976);

    // ---- the plugin reference ---------------------------------------------------------------

    [Fact]
    public void A_plugin_reference_never_reads_as_a_package_and_round_trips_its_guid()
    {
        Assert.Equal("plugin:" + Ecf, RemotePluginRef.Of(Ecf));
        Assert.True(RemotePluginRef.TryGuid("plugin:" + Ecf, out string guid));
        Assert.Equal(Ecf, guid);
        Assert.False(RemoteInstallWire.IsValidPackage(RemotePluginRef.Of(Ecf)));
        Assert.False(RemotePluginRef.IsRef("team-Sample"));
        Assert.Equal("", RemotePluginRef.Of("has space"));
        Assert.Equal("", RemotePluginRef.Of(new string('a', RemotePluginRef.MaxGuidChars + 1)));
        Assert.False(RemotePluginRef.TryGuid("plugin:../x", out _));
    }

    [Fact]
    public void Only_an_uninstall_may_name_a_plugin()
    {
        var limiter = new RemoteInstallRateLimiter();
        Assert.Equal(RemoteInstallReason.None, RemoteInstallPolicy.Check(
            new RemoteInstallRequest(1, RemoteInstallOp.Uninstall, "plugin:" + Ecf, ""), Admin, RemoteInstallSettings.Default, limiter, false, 0));
        Assert.Equal(RemoteInstallReason.BadPackage, RemoteInstallPolicy.Check(
            new RemoteInstallRequest(2, RemoteInstallOp.Install, "plugin:" + Ecf, "1.0.0"), Admin, RemoteInstallSettings.Default, limiter, false, 61));
    }

    [Fact]
    public void The_client_sends_a_plugin_uninstall_and_finds_the_server_s_plugin_row()
    {
        var client = new RemoteInstallClient();
        var sent = new List<RemoteInstallRequest>();
        client.Send = bytes =>
        {
            Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var r));
            sent.Add(r);
            return true;
        };
        client.Accept(new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true,
            new[] { new RemoteServerRow("plugin:" + Ecf, "0.11.0", RemoteRowState.Hot, "") }));

        Assert.Equal(RemoteRowState.Hot, client.PluginRowFor(Ecf)!.State);
        Assert.Null(client.PluginRowFor("other.plugin"));
        Assert.Equal(0, client.Begin(RemoteInstallOp.Install, "plugin:" + Ecf, "1.0.0", false, out string? refusal));
        Assert.NotNull(refusal);
        Assert.NotEqual(0, client.Begin(RemoteInstallOp.Uninstall, "plugin:" + Ecf, "", false, out _));
        Assert.Equal("plugin:" + Ecf, sent.Single(r => r.Op == RemoteInstallOp.Uninstall).Package);
    }

    // ---- the server's identification ----------------------------------------------------------

    [Fact]
    public void The_server_decides_ledger_then_reload_file_then_waiting_request_then_ordinary_plugin()
    {
        string reload = Path.Combine(Path.GetTempPath(), "x", "BepInEx", "progenitor-reload", "Mod.dll");
        var ledger = new[] { new ServerLedgerFile(reload, "team-Mod", RemoteRowState.Loaded) };
        var certain = new PluginIdentification { Certain = true, Folder = "plugins/a" };

        Assert.Equal(ServerPluginRemoval.None, ServerPluginRemovals.Decide(new ServerPluginFact { Id = "progenitor", Framework = true, ReloadFile = reload }, ledger).Removal);
        var ledgered = ServerPluginRemovals.Decide(new ServerPluginFact { Id = "team.mod", ReloadFile = reload }, ledger);
        Assert.Equal(ServerPluginRemoval.Ledger, ledgered.Removal);
        Assert.Equal("team-Mod", ledgered.Package);
        Assert.Equal(RemoteRowState.Loaded, ServerPluginRemovals.RowState(ledgered));
        Assert.Equal(ServerPluginRemoval.Ledger, ServerPluginRemovals.Decide(new ServerPluginFact { Id = "team.mod", Location = reload }, ledger).Removal);

        var hot = ServerPluginRemovals.Decide(new ServerPluginFact { Id = Ecf, ReloadFile = reload + "x" }, ledger);
        Assert.Equal(ServerPluginRemoval.ReloadNow, hot.Removal);
        Assert.Equal(RemoteRowState.Hot, ServerPluginRemovals.RowState(hot));

        Assert.Equal(ServerPluginRemoval.RemovalPending,
            ServerPluginRemovals.Decide(new ServerPluginFact { Id = "a", Plain = certain, RemovalRequested = true }, null).Removal);
        var next = ServerPluginRemovals.Decide(new ServerPluginFact { Id = "a", Plain = certain }, null);
        Assert.Equal(ServerPluginRemoval.NextStart, next.Removal);
        Assert.Equal(RemoteRowState.Loaded, ServerPluginRemovals.RowState(next));

        var elsewhere = ServerPluginRemovals.Decide(
            new ServerPluginFact { Id = "a", Plain = new PluginIdentification { Why = "It is not in a folder of its own." } }, null);
        Assert.Equal(ServerPluginRemoval.None, elsewhere.Removal);
        Assert.Equal("It is not in a folder of its own", elsewhere.Why);
        Assert.Equal(RemoteRowState.None, ServerPluginRemovals.RowState(elsewhere));

        var rows = ServerPluginRemovals.Rows(new[]
        {
            new ServerPluginFact { Id = "progenitor.dev", Framework = true },
            new ServerPluginFact { Id = Ecf, Version = "0.11.0", ReloadFile = reload + "x" },
            new ServerPluginFact { Id = "bad id" },
        }, ledger);
        var row = Assert.Single(rows);
        Assert.Equal("plugin:" + Ecf, row.Package);
        Assert.Equal("0.11.0", row.Version);
    }

    // ---- the Mods page plan -------------------------------------------------------------------

    private static ModUninstallPlan Plan(
        bool clientPresent = false, ClientRemover client = ClientRemover.None, bool serverPresent = true,
        ServerRoute? route = null, RemoteServerRow? ledgerRow = null, RemoteServerRow? pluginRow = null,
        bool localOurs = false, ServerIdentity? server = null, bool serverOnly = false, bool hot = false,
        RemoteInstallProgress? lastRefusal = null) =>
        ModUninstallPlan.Decide(new ModUninstallFacts
        {
            ClientPresent = clientPresent,
            Client = client,
            ClientHot = hot,
            ServerPresent = serverPresent,
            Route = route ?? Remote,
            LedgerRow = ledgerRow,
            PluginRow = pluginRow,
            LocalServerOurs = localOurs,
            Server = server ?? TestServer,
            ServerOnlyMod = serverOnly,
            LastRefusal = lastRefusal,
        });

    /// <summary>
    /// The live case: no client copy, the server's reload-folder copy of a server-only mod. Uninstall
    /// goes to the connected server by plugin id, and the box names that server and says it goes now.
    /// What this would catch: the client seam's "not ours", or the "your local server" sentence.
    /// </summary>
    [Fact]
    public void A_server_only_reload_copy_goes_to_the_connected_server_and_the_box_says_so()
    {
        var plan = Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "0.11.0", RemoteRowState.Hot, ""), serverOnly: true);
        Assert.True(plan.CanAct);
        Assert.Equal(ClientRemover.None, plan.Client);
        Assert.Equal(ServerRemover.Remote, plan.Server);
        Assert.Equal("plugin:" + Ecf, plan.RemotePackage);
        Assert.Equal(
            "Removed from Forms Test now; its reloader unloads it, nothing to restart." + ModUninstallPlan.PlayersUnaffected,
            plan.Body);
        Assert.DoesNotContain("local server", plan.Body);
        Assert.DoesNotContain("your client", plan.Body);
    }

    [Fact]
    public void Each_server_row_state_says_what_the_server_will_do()
    {
        Assert.Equal("Removed from Forms Test at its next restart (or now, if it was hot loaded there).",
            Plan(ledgerRow: new RemoteServerRow("team-Mod", "1.0.0", RemoteRowState.Loaded, "")).Body);
        // A plugin row in state loaded is either a ledgered copy or an ordinary plugin: one truthful sentence for both.
        Assert.Equal("Removed from Forms Test at its next restart (or now, if it was hot loaded there).",
            Plan(pluginRow: new RemoteServerRow("plugin:a", "1.0.0", RemoteRowState.Loaded, "")).Body);
        Assert.Equal("The copy waiting on Forms Test is deleted now; nothing loads there at its next restart.",
            Plan(ledgerRow: new RemoteServerRow("team-Mod", "1.0.0", RemoteRowState.Pending, "")).Body);
        // The ledger row wins over the plugin row: it is the Install tab's own row.
        var both = Plan(ledgerRow: new RemoteServerRow("team-Mod", "1", RemoteRowState.Loaded, ""), pluginRow: new RemoteServerRow("plugin:a", "1", RemoteRowState.Hot, ""));
        Assert.Equal("team-Mod", both.RemotePackage);
        Assert.False(both.RemoteByPlugin);
    }

    /// <summary>Nothing removable means no confirmation: the button is disabled and says why.</summary>
    [Fact]
    public void Nothing_removable_disables_Uninstall_with_the_reason()
    {
        var notAdmin = Plan(route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin));
        Assert.False(notAdmin.CanAct);
        Assert.Equal(ServerRouteCopy.NotAdmin + ".", notAdmin.WhyNot);
        Assert.Equal("", notAdmin.Body);

        Assert.Equal(ModUninstallPlan.ServerUnreported("Forms Test"), Plan().WhyNot);
        Assert.Equal(ModUninstallPlan.ServerManagedElsewhere("Forms Test"),
            Plan(pluginRow: new RemoteServerRow("plugin:a", "1", RemoteRowState.None, "")).WhyNot);
        Assert.Equal(ModUninstallPlan.ServerAlreadyPending("Forms Test"),
            Plan(pluginRow: new RemoteServerRow("plugin:a", "1", RemoteRowState.UninstallPending, "")).WhyNot);
        Assert.Equal(ModUninstallPlan.ServerNotConnected, Plan(route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer)).WhyNot);
        Assert.Equal(ModUninstallPlan.ServerNotProgenitor, Plan(route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotProgenitor)).WhyNot);

        var notOurs = Plan(clientPresent: true, serverPresent: false);
        Assert.False(notOurs.CanAct);
        Assert.Equal(CatalogUninstallSeam.NotOurs, notOurs.WhyNot);
    }

    [Fact]
    public void The_client_copy_keeps_today_s_sentence_and_both_sides_are_named()
    {
        Assert.Equal(ModPageCopy.UninstallBody, Plan(clientPresent: true, client: ClientRemover.Seam, serverPresent: false).Body);
        Assert.Equal(ModPageCopy.UninstallBodyHot, Plan(clientPresent: true, client: ClientRemover.Seam, serverPresent: false, hot: true).Body);

        var both = Plan(clientPresent: true, client: ClientRemover.Seam, ledgerRow: new RemoteServerRow("team-Mod", "1", RemoteRowState.Loaded, ""));
        Assert.Equal(ModPageCopy.UninstallBody + " Removed from Forms Test at its next restart (or now, if it was hot loaded there).", both.Body);

        // A client copy that is not ours stays, and the box says so; the server copy still goes.
        var serverOnly = Plan(clientPresent: true, ledgerRow: new RemoteServerRow("team-Mod", "1", RemoteRowState.Hot, ""));
        Assert.True(serverOnly.CanAct);
        Assert.Contains("Your client's copy stays", serverOnly.Body);

        // A server copy that cannot go is named as staying.
        var clientOnly = Plan(clientPresent: true, client: ClientRemover.Seam, route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin));
        Assert.Contains("The copy on the connected server stays.", Plan(clientPresent: true, client: ClientRemover.Seam,
            route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin), server: ServerIdentity.Connected("", "")).Body);
        Assert.True(clientOnly.CanAct);
    }

    /// <summary>The local folder's copy is named by its server's name, never "your local server".</summary>
    [Fact]
    public void The_local_folder_route_names_its_server()
    {
        var local = Plan(clientPresent: true, client: ClientRemover.Seam, route: new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None),
            localOurs: true, server: ServerIdentity.Local("Forms Test", "C:\\srv"));
        Assert.Equal(ServerRemover.LocalSeam, local.Server);
        Assert.EndsWith("Also removed from Forms Test at its next restart.", local.Body);
        Assert.False(Plan(route: new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None)).CanAct);
    }

    [Fact]
    public void The_server_s_answer_becomes_the_page_s_note_and_a_refusal_is_spelled_out()
    {
        RemoteInstallProgress Step(RemoteInstallStage stage, RemoteInstallReason reason = RemoteInstallReason.None, string detail = "") =>
            new(1, RemoteInstallOp.Uninstall, stage, reason, "plugin:" + Ecf, "", detail);

        Assert.Equal("Sent to Forms Test…", ModUninstallPlan.ResultNote(Step(RemoteInstallStage.Accepted), TestServer));
        Assert.Equal("Removed from Forms Test.", ModUninstallPlan.ResultNote(Step(RemoteInstallStage.Removed), TestServer));
        Assert.Equal("Removed from Forms Test at its next restart.", ModUninstallPlan.ResultNote(Step(RemoteInstallStage.PendingRestart), TestServer));
        Assert.Equal("Forms Test did not remove it: managed elsewhere: shared folder.",
            ModUninstallPlan.ResultNote(Step(RemoteInstallStage.Refused, RemoteInstallReason.Unavailable, "managed elsewhere: shared folder"), TestServer));
        string force = ModUninstallPlan.ResultNote(
            Step(RemoteInstallStage.Refused, RemoteInstallReason.Unavailable, RemoteUninstallForce.IndexUnreadablePrefix + "the download failed (dns)"), TestServer);
        Assert.EndsWith("Uninstall again to remove it without that check.", force);
    }

    /// <summary>After a refusal the admin may override, the next Uninstall is sent with force and says so.</summary>
    [Fact]
    public void After_an_overridable_refusal_the_plan_forces_and_says_the_check_is_skipped()
    {
        var refusal = new RemoteInstallProgress(1, RemoteInstallOp.Uninstall, RemoteInstallStage.Refused, RemoteInstallReason.Unavailable,
            "team-Mod", "", RemoteUninstallForce.IndexUnreadablePrefix + "x");
        var plan = Plan(ledgerRow: new RemoteServerRow("team-Mod", "1", RemoteRowState.Loaded, ""), lastRefusal: refusal);
        Assert.True(plan.RemoteForce);
        Assert.Contains("without the dependency check", plan.Body);
        Assert.False(Plan(ledgerRow: new RemoteServerRow("team-Mod", "1", RemoteRowState.Loaded, "")).RemoteForce);
        Assert.False(RemoteUninstallForce.Offers(new RemoteInstallProgress(1, RemoteInstallOp.Uninstall, RemoteInstallStage.Refused,
            RemoteInstallReason.NotAdmin, "team-Mod", "", "")));
        Assert.True(RemoteUninstallForce.Offers(new RemoteInstallProgress(1, RemoteInstallOp.Uninstall, RemoteInstallStage.Refused,
            RemoteInstallReason.HasDependents, "team-Mod", "", "needed by a-B")));
    }

    // ---- the page model and the framework line -------------------------------------------------

    [Fact]
    public void A_plan_that_cannot_act_draws_a_disabled_Uninstall_with_its_reason()
    {
        var plan = Plan(route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin));
        var model = new ModPageModel(Ecf, "ExampleCommandFramework", "0.11.0", ModProvenance.Server, ModPageState.Installed,
            ModPageListing.None, false, false, true, false, actionable: true) { Uninstall = plan };
        Assert.Equal(ModPageCopy.Uninstall, model.ActionLabel);
        Assert.False(model.ActionEnabled);
        Assert.Equal(plan.WhyNot, model.ActionReason);

        var facts = new ModFrameworkFacts { PluginId = Ecf, ActionCaption = model.ActionLabel, ActionTone = LabelTone.Orange }
            .WithActionDisabled(model.ActionReason);
        var line = ModFrameworkLine.Compose(facts);
        Assert.False(line.ActionEnabled);
        Assert.Equal(plan.WhyNot, line.Readback()["actionReason"]);
        var action = line.BuildRow("k", 800).Cells.Single(c => c.Key == ModFrameworkLine.CellAction);
        Assert.Equal(CellTone.Disabled, action.Tone);

        var able = new ModPageModel(Ecf, "ExampleCommandFramework", "0.11.0", ModProvenance.Server, ModPageState.Installed,
            ModPageListing.None, false, false, true, false, actionable: true)
        { Uninstall = Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "0.11.0", RemoteRowState.Hot, "")) };
        Assert.True(able.ActionEnabled);
        Assert.Equal(able.Uninstall!.Body, able.PromptBody(ModPromptKind.Uninstall));
    }

    /// <summary>UNINSTREQ1: the notices update the rule in force (the admins may change it mid-session).</summary>
    [Fact]
    public void A_notice_tells_the_client_the_rule_in_force()
    {
        try
        {
            Networking.ClientModReportClient.NoteServerRequest(true, true, Networking.ServerMidSessionRule.Warn);
            Assert.Equal(Networking.ServerMidSessionRule.Warn, Networking.ClientModReportClient.MidSessionRule);
            Networking.ClientModReportClient.NoteNoticeRule(Networking.RecheckNoticeKind.Disconnect);
            Assert.Equal(Networking.ServerMidSessionRule.Disconnect, Networking.ClientModReportClient.MidSessionRule);
            // A warning about a requirement added mid-session is told as a warning even under Disconnect: no change.
            var added = new Networking.RecheckItem { Key = "a", Kind = Networking.ClientModProblem.KindMissing, Added = true };
            Networking.ClientModReportClient.NoteNoticeRule(Networking.RecheckNoticeKind.Warn, new[] { added });
            Assert.Equal(Networking.ServerMidSessionRule.Disconnect, Networking.ClientModReportClient.MidSessionRule);
            var lost = new Networking.RecheckItem { Key = "a", Kind = Networking.ClientModProblem.KindMissing };
            Networking.ClientModReportClient.NoteNoticeRule(Networking.RecheckNoticeKind.Warn, new[] { lost });
            Assert.Equal(Networking.ServerMidSessionRule.Warn, Networking.ClientModReportClient.MidSessionRule);
            // An older server: unknown.
            Networking.ClientModReportClient.NoteServerRequest(true, true);
            Assert.Equal(Networking.ServerMidSessionRule.Unknown, Networking.ClientModReportClient.MidSessionRule);
        }
        finally { Networking.ClientModReportClient.Reset(); }
    }

    /// <summary>
    /// UNINSTREQ1 (standing rule 2026-10-07): removing this client's copy of a mod the connected server requires draws a
    /// <c>Required by</c> fact saying what the server does; removing only the server's copy does not. Game-free; not
    /// live-verified.
    /// </summary>
    [Fact]
    public void Removing_the_client_copy_of_a_required_mod_says_what_the_server_does()
    {
        Assert.Equal("Night Garden: nothing checks it again while you stay, but your next join is refused until it's back.",
            Networking.RequiredHereFact.Value("Night Garden", rechecks: false));
        Assert.Equal("Night Garden: it checks your mods while you play, so you'll be warned in game (or disconnected, if its admins chose that); your next join is refused until it's back.",
            Networking.RequiredHereFact.Value("Night Garden", rechecks: true));
        // The server's own rule, from its request's capability row or its notices.
        Assert.Equal("Night Garden: it disconnects players who remove a mod it requires unless it's back within 15 seconds; your next join is refused until it's back.",
            Networking.RequiredHereFact.Value("Night Garden", rechecks: true, Networking.ServerMidSessionRule.Disconnect));
        Assert.Equal("Night Garden: you'll be warned in game and can keep playing; your next join is refused until it's back.",
            Networking.RequiredHereFact.Value("Night Garden", rechecks: true, Networking.ServerMidSessionRule.Warn));
        // Review (MEDIUM): a must-have row of the join check is judged on every answer this client sends, so it never
        // reads "nothing changes"; with the policy too, both are said.
        Assert.Equal("Night Garden: " + Networking.RequiredHereFact.HandshakeClause + "; your next join is refused until it's back.",
            Networking.RequiredHereFact.Value("Night Garden", rechecks: true, Networking.ServerMidSessionRule.Disconnect, policy: false, handshake: true));
        Assert.Contains(Networking.RequiredHereFact.HandshakeClause,
            Networking.RequiredHereFact.Value("Night Garden", rechecks: true, Networking.ServerMidSessionRule.Warn, policy: true, handshake: true), StringComparison.Ordinal);
        Assert.Equal(Networking.ServerMidSessionRule.Disconnect, Networking.ClientModReportClient.RuleOf(Networking.Handshake.Capability.PolicyRecheck | Networking.Handshake.Capability.PolicyDisconnects));
        Assert.Equal(Networking.ServerMidSessionRule.Warn, Networking.ClientModReportClient.RuleOf(Networking.Handshake.Capability.PolicyWarns));
        Assert.Equal(Networking.ServerMidSessionRule.Unknown, Networking.ClientModReportClient.RuleOf(Networking.Handshake.Capability.PolicyRecheck));
        Assert.Equal(Networking.Handshake.Capability.PolicyDisconnects,
            Networking.ClientModReportClient.MidSessionBit(Networking.MidSessionPolicy.Disconnect));
        Assert.Equal(Networking.Handshake.Capability.PolicyWarns, Networking.ClientModReportClient.MidSessionBit(Networking.MidSessionPolicy.Warn));
        try
        {
            Networking.RequiredHereFact.Probe = id => id == Ecf ? ("Night Garden", true, false) : null;
            Networking.RequiredHereFact.RechecksProbe = () => false;
            Assert.Equal("", Networking.RequiredHereFact.ValueFor("other.mod"));
            string value = Networking.RequiredHereFact.ValueFor(Ecf);
            Assert.StartsWith("Night Garden: ", value, StringComparison.Ordinal);

            ModPageModel Page(ModUninstallPlan? plan) => new(Ecf, "ExampleCommandFramework", "0.11.0", ModProvenance.Server, ModPageState.Installed,
                ModPageListing.None, false, true, true, false, actionable: true) { Uninstall = plan, RequiredBy = value };
            var both = Page(Plan(clientPresent: true, client: ClientRemover.Seam, pluginRow: new RemoteServerRow("plugin:" + Ecf, "0.11.0", RemoteRowState.Hot, "")));
            var fact = Assert.Single(both.PromptFacts(ModPromptKind.Uninstall)!);
            Assert.Equal(("Required by", value), (fact.Label, fact.Value));
            Assert.Null(Page(Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "0.11.0", RemoteRowState.Hot, ""))).PromptFacts(ModPromptKind.Uninstall));
            Assert.Null(both.PromptFacts(ModPromptKind.Reactivate));
        }
        finally
        {
            Networking.RequiredHereFact.Probe = null;
            Networking.RequiredHereFact.RechecksProbe = null;
        }
    }

    /// <summary>
    /// F10 (live 2026-10-07): a server copy just hot loaded into the local server folder goes now, decided from that
    /// server's own state, and the page removes exactly the sides its box named (F4).
    /// </summary>
    [Fact]
    public void A_local_server_copy_its_reloader_holds_goes_now_and_only_named_sides_are_removed()
    {
        var local = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        var folder = ServerIdentity.Local("Forms Test", @"C:srv");
        var now = ModUninstallPlan.Decide(new ModUninstallFacts
        {
            ServerPresent = true, Route = local, LocalServerOurs = true, LocalServerHot = true, Server = folder, ServerOnlyMod = true,
        });
        Assert.True(now.CanAct);
        Assert.True(now.ServerUnloadsNow);
        Assert.Contains("now; its reloader unloads it", now.Body, StringComparison.Ordinal);
        var later = ModUninstallPlan.Decide(new ModUninstallFacts
        {
            ServerPresent = true, Route = local, LocalServerOurs = true, LocalServerHot = false, Server = folder, ServerOnlyMod = true,
        });
        Assert.Contains("at its next restart", later.Body, StringComparison.Ordinal);

        string run = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ServerUninstall.cs");
        Assert.Contains("seam.UninstallSides(pluginId, model.Hot, clientSide, serverSide)", run, StringComparison.Ordinal);
        Assert.Contains("LocalServerHot = owns && serverLedger && SeamServerUnloadsNow(seam, pluginId)", run, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review (L8): the ledger lookup is marked served only once a catalog exists to serve into, so a page built before the
    /// catalog asks again. Source-checked; not live-verified.
    /// </summary>
    [Fact]
    public void The_ledger_lookup_is_marked_served_only_with_a_catalog()
    {
        string run = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ServerUninstall.cs");
        int guard = run.IndexOf("if (_catalog is not InstallCatalogService service) return;", StringComparison.Ordinal);
        int mark = run.IndexOf("_ledgerLookupServed = true;", StringComparison.Ordinal);
        Assert.True(guard > 0 && mark > guard);
    }

    /// <summary>F1 (live 2026-10-07): a mod its install record names is never called "not ours".</summary>
    [Fact]
    public void A_ledgered_mod_whose_listing_is_not_loaded_is_never_called_not_ours()
    {
        var plan = ModUninstallPlan.Decide(new ModUninstallFacts
        {
            ClientPresent = true, Client = ClientRemover.None, ClientLedgeredUnresolved = true, ServerPresent = false, Route = Remote,
        });
        Assert.False(plan.CanAct);
        Assert.Equal(ModUninstallPlan.ClientLedgerNotMapped, plan.ClientWhyNot);
        Assert.DoesNotContain("did not install", plan.WhyNot, StringComparison.Ordinal);
    }

    // ---- the Install tab's cells ----------------------------------------------------------------

    /// <summary>
    /// A refused remote uninstall stays on the Server cell (it was only in the log), with
    /// "Uninstall anyway" when the server allows the override; Simple shows the same.
    /// </summary>
    [Fact]
    public void A_refused_uninstall_is_on_the_cell_with_the_override()
    {
        var half = new SideHalf
        {
            State = HalfState.Installed, Version = "0.1.0", CanUninstall = true,
            UninstallRefused = RemoteUninstallForce.RefusedWord, CanForceUninstall = true,
        };
        var cells = SideCells.Decide(new SideCellInputs
        {
            Side = PackageSide.Server,
            Client = SideHalf.None,
            Server = half,
            Route = Remote,
            Target = ServerTargetKind.Server,
            Latest = "0.1.0",
        });
        Assert.Equal("Installed 0.1.0" + SideCells.Dot + RemoteUninstallForce.RefusedWord, cells.Server.Text);
        Assert.Equal(SideCellTone.Warn, cells.Server.Tone);
        Assert.True(cells.Server.Has(SideButton.UninstallAnyway));
        Assert.False(cells.Server.Has(SideButton.Uninstall));
        Assert.Equal(RemoteUninstallForce.AnywayCaption, SideCells.Caption(SideButton.UninstallAnyway));

        var simple = InstallSimpleRules.Decide(cells, PackageSide.Server, actsOnServer: true);
        Assert.Equal(InstallSimpleRules.KindRefused, simple.Kind);
        Assert.Equal(SideButton.UninstallAnyway, simple.Button);
        Assert.Equal(InstallSimpleTargets.Server, simple.Targets);
    }

    [Fact]
    public void A_reload_folder_copy_reads_installed_there_with_Uninstall()
    {
        var cells = SideCells.Decide(new SideCellInputs
        {
            Side = PackageSide.Client,
            Client = new SideHalf { State = HalfState.Installed, Version = "0.1.0", CanUninstall = true, Location = InstallModsCopy.InReloadFolder },
            Server = SideHalf.None,
            Route = new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer),
            Latest = "0.1.0",
        });
        Assert.Equal("Installed 0.1.0" + SideCells.Dot + InstallModsCopy.InReloadFolder, cells.Client.Text);
        Assert.True(cells.Client.Has(SideButton.Uninstall));
    }

    // ---- review fixes ---------------------------------------------------------------------------

    /// <summary>
    /// A reload-folder DLL that declares other plugins is never deleted for one of them, and a GUID
    /// with a second copy in BepInEx/plugins is never answered "removed" while that copy stays.
    /// </summary>
    [Fact]
    public void A_shared_or_doubled_reload_copy_is_not_removable_by_id()
    {
        var shared = ServerPluginRemovals.Decide(new ServerPluginFact { Id = "a.one", ReloadFile = "C:\\r\\Both.dll", AlsoDeclares = new[] { "a.two" } }, null);
        Assert.Equal(ServerPluginRemoval.None, shared.Removal);
        Assert.Contains("a.two", shared.Why);
        var doubled = ServerPluginRemovals.Decide(new ServerPluginFact { Id = "a.one", ReloadFile = "C:\\r\\One.dll", LoadedFromPlugins = true }, null);
        Assert.Equal(ServerPluginRemoval.None, doubled.Removal);
        Assert.Equal(ServerPluginRemovals.WhyTwoCopies, doubled.Why);
    }

    /// <summary>Yes sends exactly the plan the box showed; a changed plan is refused, not substituted.</summary>
    [Fact]
    public void A_changed_plan_is_not_the_same_action()
    {
        var hot = Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "1", RemoteRowState.Hot, ""));
        Assert.True(ModUninstallPlan.SameAction(hot, Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "1", RemoteRowState.Hot, ""))));
        Assert.False(ModUninstallPlan.SameAction(hot, Plan(pluginRow: new RemoteServerRow("plugin:" + Ecf, "1", RemoteRowState.Loaded, ""))));
        Assert.False(ModUninstallPlan.SameAction(hot, Plan(clientPresent: true, client: ClientRemover.Seam,
            pluginRow: new RemoteServerRow("plugin:" + Ecf, "1", RemoteRowState.Hot, ""))));
        Assert.False(ModUninstallPlan.SameAction(hot, null));
    }

    /// <summary>
    /// Re-review: the "declares other plugins" refusal is made in the deleting step itself, reading the
    /// file as it is then, so a DLL swapped after the check is refused and kept.
    /// </summary>
    [Fact]
    public void The_server_s_reload_delete_refuses_a_shared_file_in_the_same_step()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-srvuninst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "Mod.dll");
            File.WriteAllText(file, "placeholder");
            IReadOnlyList<string> Two(string _) => new[] { "a.one", "a.two" };
            var refused = ReloadFolderPlugins.Remove(dir, "a.one", file, Two, refuseShared: true);
            Assert.False(refused.Ok);
            Assert.Contains("a.two", refused.Message);
            Assert.True(File.Exists(file));
            // The Mods page's own removal names the others in its box and still takes the file.
            Assert.True(ReloadFolderPlugins.Remove(dir, "a.one", file, Two).Ok);
            Assert.False(File.Exists(file));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
