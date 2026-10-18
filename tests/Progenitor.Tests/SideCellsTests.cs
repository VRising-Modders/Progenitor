using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>INSTALLSIDES1: the per-side decision table behind the Install row's Client and Server cells.</summary>
public sealed class SideCellsTests
{
    private static readonly ServerRoute Local = new(ServerRouteKind.Local, ServerRouteBlock.None);
    private static readonly ServerRoute Remote = new(ServerRouteKind.Remote, ServerRouteBlock.None);
    private static readonly ServerRoute NotAdmin = new(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
    private static readonly ServerRoute NoServerRoute = new(ServerRouteKind.None, ServerRouteBlock.NoServer);
    private static readonly ServerRoute Unreachable = new(ServerRouteKind.Remote, ServerRouteBlock.Unreachable, "timed out");

    private static SideHalf Absent => new() { State = HalfState.Absent };

    private static SideHalf Installed(string v, bool uninstall = true, string managed = "", bool progenitor = true) =>
        new() { State = HalfState.Installed, Version = v, CanUninstall = uninstall, Managed = managed, ProgenitorManaged = progenitor };

    private static SideHalf Pending(string v = "1.0.0") =>
        new() { State = HalfState.Pending, Version = v, CanCancelInstall = true, ProgenitorManaged = true };

    private static SideHalf Removal(string v = "1.0.0") =>
        new() { State = HalfState.RemovalPending, Version = v, CanCancelUninstall = true };

    private static SideCellsDecision D(
        SideHalf client,
        SideHalf? server = null,
        ServerTargetKind target = ServerTargetKind.Server,
        ServerRoute? route = null,
        PackageSide side = PackageSide.Both,
        bool needsBoth = false,
        string latest = "1.0.0",
        bool shared = true,
        AdminOffer admin = AdminOffer.None) =>
        SideCells.Decide(new SideCellInputs
        {
            Side = side,
            NeedsBoth = needsBoth,
            Latest = latest,
            Client = client,
            Server = server ?? Absent,
            Target = target,
            PrivateShared = shared,
            Route = route ?? Local,
            Admin = admin,
        });

    private static SideButton[] B(params SideButton[] b) => b;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnected_remote_target_hides_stale_server_state_and_actions(bool previouslyInstalled)
    {
        var d = D(Absent, previouslyInstalled ? Installed("1.0.0") : Absent,
            route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.Disconnected, forcedRemote: true));
        Assert.Equal("Connect to manage", d.Server.Text);
        Assert.True(d.Server.Grayed);
        Assert.Empty(d.Server.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);
        Assert.Contains(SideButton.Install, d.Client.Buttons);
    }

    /// <summary>Review of PR 132: a request still in flight when the player disconnects keeps its progress and Cancel.</summary>
    [Fact]
    public void A_server_step_in_flight_keeps_its_progress_and_cancel_after_a_disconnect()
    {
        var installing = new SideHalf { Busy = "Downloading…", BusyCancelable = true };
        var d = D(Absent, installing,
            route: new ServerRoute(ServerRouteKind.None, ServerRouteBlock.Disconnected, forcedRemote: true));
        Assert.Equal("Downloading…", d.Server.Text);
        Assert.Equal(SideCells.KindBusy, d.Server.Kind);
        Assert.Equal(B(SideButton.CancelInstall), d.Server.Buttons);
    }

    [Fact]
    public void Nothing_installed_offers_install_on_each_side_and_the_both_shortcut()
    {
        var d = D(Absent, Absent);
        Assert.Equal(SideCells.NotInstalled, d.Client.Text);
        Assert.Equal(SideCellTone.Muted, d.Client.Tone);
        Assert.Equal(B(SideButton.Install), d.Client.Buttons);
        Assert.Equal(SideCells.NotInstalled, d.Server.Text);
        Assert.Equal(B(SideButton.Install), d.Server.Buttons);
        Assert.Equal(SideButton.Install, d.Shortcut);
        Assert.Equal(SideCells.InstallBothCaption, SideCells.ShortcutCaption(d.Shortcut));
    }

    [Fact]
    public void Installed_reads_the_version_in_ok_tone_with_uninstall()
    {
        var d = D(Installed("2.4.0"), Installed("2.4.0"), latest: "2.4.0");
        Assert.Equal("Installed 2.4.0", d.Client.Text);
        Assert.Equal(SideCellTone.Ok, d.Client.Tone);
        Assert.Equal(B(SideButton.Uninstall), d.Client.Buttons);
        Assert.Equal("Installed 2.4.0", d.Server.Text);
        Assert.Equal(B(SideButton.Uninstall), d.Server.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);
    }

    [Fact]
    public void A_newer_listing_offers_update_beside_uninstall_on_that_side_only()
    {
        var d = D(Installed("1.0.3"), Installed("1.1.0"), latest: "1.1.0");
        Assert.Equal("1.0.3 · update 1.1.0", d.Client.Text);
        Assert.Equal(SideCells.KindUpdate, d.Client.Kind);
        Assert.Equal(SideCellTone.Warn, d.Client.Tone);
        Assert.Equal(B(SideButton.Update, SideButton.Uninstall), d.Client.Buttons);
        Assert.Equal("Installed 1.1.0", d.Server.Text);
        Assert.Equal(B(SideButton.Uninstall), d.Server.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);
    }

    [Fact]
    public void Both_sides_behind_the_listing_get_the_update_both_shortcut()
    {
        var d = D(Installed("1.0.0"), Installed("1.0.0"), latest: "1.2.0");
        Assert.Equal(SideButton.Update, d.Shortcut);
        Assert.Equal(SideCells.UpdateBothCaption, SideCells.ShortcutCaption(d.Shortcut));
    }

    [Fact]
    public void A_declared_both_sided_mod_missing_a_half_says_which_side_has_it()
    {
        var d = D(Absent, Installed("1.0.0"), needsBoth: true);
        Assert.Equal(SideCells.MissingServerHasIt, d.Client.Text);
        Assert.Equal(SideCells.KindMissing, d.Client.Kind);
        Assert.Equal(SideCellTone.Warn, d.Client.Tone);
        Assert.Equal(B(SideButton.Install), d.Client.Buttons);

        var e = D(Installed("1.0.0"), Absent, needsBoth: true);
        Assert.Equal(SideCells.MissingClientHasIt, e.Server.Text);
        Assert.Equal(B(SideButton.Install), e.Server.Buttons);
    }

    [Fact]
    public void Tag_only_sides_are_never_missing_or_older()
    {
        var d = D(Absent, Installed("1.0.0"), needsBoth: false);
        Assert.Equal(SideCells.NotInstalled, d.Client.Text);
        Assert.Equal(SideCellTone.Muted, d.Client.Tone);

        var e = D(Installed("1.0.0"), Installed("1.1.0"), needsBoth: false, latest: "1.0.0");
        Assert.Equal("Installed 1.0.0", e.Client.Text);
        Assert.Equal(SideCellTone.Ok, e.Client.Tone);
    }

    [Fact]
    public void A_mismatch_on_a_declared_mod_updates_the_older_side()
    {
        var d = D(Installed("1.3.2"), Installed("1.4.0"), needsBoth: true, latest: "1.3.0");
        Assert.Equal("1.3.2 · older than server", d.Client.Text);
        Assert.Equal(SideCells.KindOlder, d.Client.Kind);
        Assert.Equal(B(SideButton.Update, SideButton.Uninstall), d.Client.Buttons);
        Assert.Equal("Installed 1.4.0", d.Server.Text);

        var e = D(Installed("1.4.0"), Installed("1.3.2"), needsBoth: true, latest: "1.3.0");
        Assert.Equal("1.3.2 · older than client", e.Server.Text);
        Assert.Equal(B(SideButton.Update, SideButton.Uninstall), e.Server.Buttons);
    }

    [Fact]
    public void A_side_the_mod_does_not_run_on_is_not_needed_with_no_button()
    {
        var server = D(Absent, Absent, side: PackageSide.Server);
        Assert.Equal(SideCells.NoClientSide, server.Client.Text);
        Assert.Empty(server.Client.Buttons);
        Assert.Equal(B(SideButton.Install), server.Server.Buttons);
        Assert.Equal(SideButton.None, server.Shortcut);

        var client = D(Absent, Absent, side: PackageSide.Client);
        Assert.Equal(SideCells.NoServerSide, client.Server.Text);
        Assert.Empty(client.Server.Buttons);
    }

    [Fact]
    public void A_copy_on_a_side_that_does_not_need_it_can_still_be_removed()
    {
        var d = D(Installed("1.0.0"), Absent, side: PackageSide.Server);
        Assert.Equal("Installed 1.0.0", d.Client.Text);
        Assert.Equal(B(SideButton.Uninstall), d.Client.Buttons);
    }

    [Fact]
    public void An_unknown_side_is_treated_as_running_everywhere()
    {
        var d = D(Absent, Absent, side: PackageSide.Unknown);
        Assert.Equal(SideCells.NotInstalled, d.Client.Text);
        Assert.Equal(SideCells.NotInstalled, d.Server.Text);
        Assert.Equal(SideButton.Install, d.Shortcut);
    }

    [Fact]
    public void Pending_and_removal_pending_name_the_restart_and_offer_the_cancel()
    {
        var d = D(Pending(), Pending());
        Assert.Equal(InstallStatusLabels.PendingGameRestart, d.Client.Text);
        Assert.Equal(SideCellTone.Pending, d.Client.Tone);
        Assert.Equal(B(SideButton.CancelInstall), d.Client.Buttons);
        Assert.Equal(InstallStatusLabels.PendingServerRestart, d.Server.Text);
        Assert.Equal(B(SideButton.CancelInstall), d.Server.Buttons);

        var e = D(Removal(), Removal());
        Assert.Equal(InstallStatusLabels.RemovalPendingGameRestart, e.Client.Text);
        // INSTALLSIDES2 item 1: waiting on a restart is pending (pale blue), install or removal.
        Assert.Equal(SideCellTone.Pending, e.Client.Tone);
        Assert.Equal(B(SideButton.CancelUninstall), e.Client.Buttons);
        Assert.Equal(InstallStatusLabels.RemovalPendingServerRestart, e.Server.Text);
        Assert.Equal(B(SideButton.CancelUninstall), e.Server.Buttons);
    }

    [Fact]
    public void A_pending_copy_another_tool_wrote_has_no_cancel()
    {
        var other = new SideHalf { State = HalfState.Pending, CanCancelInstall = false };
        Assert.Empty(D(other).Client.Buttons);
    }

    [Fact]
    public void Managed_elsewhere_states_it_and_offers_nothing()
    {
        var d = D(Installed("1.0.0", uninstall: false, managed: "Managed by Gale", progenitor: false));
        Assert.Equal("Installed 1.0.0 · Managed by Gale", d.Client.Text);
        Assert.Equal(SideCellTone.Muted, d.Client.Tone);
        Assert.Empty(d.Client.Buttons);
    }

    [Fact]
    public void A_managed_copy_behind_the_listing_can_still_be_updated()
    {
        var d = D(Installed("1.0.0", uninstall: false, managed: "Managed elsewhere", progenitor: false), latest: "2.0.0");
        Assert.Equal(B(SideButton.Update), d.Client.Buttons);
    }

    [Theory]
    [InlineData(ServerRouteBlock.NotAdmin, SideCells.AskServerAdmin)]
    [InlineData(ServerRouteBlock.NotAllowed, SideCells.AskServerAdmin)]
    // REMOTEUNREACH1: each block in its own words; only not-admin / not-allowed name an admin.
    [InlineData(ServerRouteBlock.RemoteOff, InstallStatusLabels.RemoteInstallsOff)]
    [InlineData(ServerRouteBlock.Pending, InstallStatusLabels.Checking)]
    [InlineData(ServerRouteBlock.NotProgenitor, InstallStatusLabels.NoProgenitor)]
    [InlineData(ServerRouteBlock.NoServer, SideCells.NoServer)]
    public void A_route_block_shows_the_state_and_the_note_and_no_button(object blockValue, string note)
    {
        var route = new ServerRoute(ServerRouteKind.None, (ServerRouteBlock)blockValue);
        var d = D(Absent, Absent, route: route);
        Assert.Equal(SideCells.NotInstalled + " · " + note, d.Server.Text);
        Assert.Equal(note, d.Server.Note);
        Assert.Empty(d.Server.Buttons);
        Assert.Equal(B(SideButton.Install), d.Client.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);

        var installed = D(Absent, Installed("1.0.0"), route: route);
        Assert.Equal("Installed 1.0.0", installed.Server.Text);
        Assert.Empty(installed.Server.Buttons);

        var behind = D(Absent, Installed("1.0.0"), route: route, latest: "2.0.0");
        Assert.Equal("1.0.0 · update 2.0.0 · " + note, behind.Server.Text);
        Assert.Empty(behind.Server.Buttons);
    }

    [Fact]
    public void An_unreachable_remote_route_still_uninstalls_but_cannot_install()
    {
        var d = D(Absent, Installed("1.0.0"), route: Unreachable, latest: "2.0.0");
        Assert.Equal(B(SideButton.Uninstall), d.Server.Buttons);
        // REMOTEUNREACH1 (live): the admin was told to ask an admin; the server is cut off from Thunderstore.
        Assert.Contains(SideCells.ServerCantReachThunderstore, d.Server.Text);
        Assert.DoesNotContain(SideCells.AskServerAdmin, d.Server.Text);
        Assert.Equal(SideCells.NotInstalled + " · " + SideCells.ServerCantReachThunderstore, D(Absent, Absent, route: Unreachable).Server.Text);
        Assert.Equal("Installed 1.0.0", D(Absent, Installed("1.0.0"), route: Unreachable).Server.Text);
        var e = D(Absent, Absent, route: Unreachable);
        Assert.Empty(e.Server.Buttons);
    }

    [Fact]
    public void The_remote_and_local_routes_both_act()
    {
        Assert.Equal(B(SideButton.Install), D(Absent, Absent, route: Remote).Server.Buttons);
        Assert.Equal(B(SideButton.Install), D(Absent, Absent, route: Local).Server.Buttons);
    }

    [Fact]
    public void An_unknown_server_half_is_muted_and_installable_only_through_a_route()
    {
        var unknown = new SideHalf { State = HalfState.Unknown };
        var d = D(Absent, unknown, route: Remote);
        Assert.Equal(SideCells.UnknownWord, d.Server.Text);
        Assert.Equal(SideCellTone.Muted, d.Server.Tone);
        Assert.Equal(B(SideButton.Install), d.Server.Buttons);

        var e = D(Absent, unknown, route: NotAdmin);
        Assert.Equal(SideCells.UnknownWord + " · " + SideCells.AskServerAdmin, e.Server.Text);
        Assert.Empty(e.Server.Buttons);
    }

    [Fact]
    public void None_target_says_no_server_or_not_needed()
    {
        var d = D(Installed("1.0.0"), Installed("1.0.0"), target: ServerTargetKind.None);
        Assert.Equal(SideCells.NoServerSelected, d.Server.Text);
        Assert.Equal(SideCells.KindNoServer, d.Server.Kind);
        Assert.True(d.Server.Grayed);
        Assert.Empty(d.Server.Buttons);
        Assert.Equal(B(SideButton.Uninstall), d.Client.Buttons);

        // INSTALLSIDES2 item 2: with None picked the Server column is grayed for every mod.
        var client = D(Absent, target: ServerTargetKind.None, side: PackageSide.Client);
        Assert.Equal(SideCells.NoServerSelected, client.Server.Text);
        Assert.True(client.Server.Grayed);
        Assert.Equal(SideButton.None, client.Shortcut);
    }

    [Fact]
    public void None_target_never_reads_missing_from_a_server_it_does_not_look_at()
    {
        var d = D(Absent, Installed("1.0.0"), target: ServerTargetKind.None, needsBoth: true);
        Assert.Equal(SideCells.NotInstalled, d.Client.Text);
    }

    [Fact]
    public void Private_games_shared_read_same_as_client_with_no_buttons()
    {
        var d = D(Installed("1.0.0", progenitor: false), target: ServerTargetKind.PrivateGames, shared: true);
        Assert.Equal(SideCells.SameAsClient, d.Server.Text);
        Assert.Empty(d.Server.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);
        var client = D(Absent, target: ServerTargetKind.PrivateGames, side: PackageSide.Client);
        Assert.Equal(SideCells.NoServerSide, client.Server.Text);
    }

    [Fact]
    public void With_private_games_picked_a_server_mod_installs_on_this_client_which_they_load()
    {
        var d = D(Absent, target: ServerTargetKind.PrivateGames, side: PackageSide.Server);
        Assert.Equal(SideCells.NotInstalled, d.Client.Text);
        Assert.Equal(B(SideButton.Install), d.Client.Buttons);
        Assert.Equal(SideCells.SameAsClient, d.Server.Text);
        // With no server at all a server mod has nowhere to run: not needed here, no server there.
        var none = D(Absent, target: ServerTargetKind.None, side: PackageSide.Server);
        Assert.Equal(SideCells.NoClientSide, none.Client.Text);
        Assert.Empty(none.Client.Buttons);
        Assert.Equal(SideCells.NoServerSelected, none.Server.Text);
    }

    [Fact]
    public void Private_games_progenitor_only_load_progenitor_installs_but_not_other_tools_plugins()
    {
        var mine = D(Installed("1.0.0", progenitor: true), target: ServerTargetKind.PrivateGames, shared: false);
        Assert.Equal(SideCells.SameAsClient, mine.Server.Text);

        var theirs = D(Installed("1.0.0", uninstall: false, managed: "Managed elsewhere", progenitor: false),
            target: ServerTargetKind.PrivateGames, shared: false);
        Assert.Equal(SideCells.NotLoadedThere, theirs.Server.Text);
        Assert.Equal(SideCellTone.Warn, theirs.Server.Tone);
        Assert.Empty(theirs.Server.Buttons);

        var none = D(Absent, target: ServerTargetKind.PrivateGames, shared: false);
        Assert.Equal(SideCells.SameAsClient, none.Server.Text);
    }

    [Fact]
    public void A_step_in_flight_shows_its_words_and_the_matching_cancel()
    {
        var installing = new SideHalf { Busy = "Downloading…", BusyCancelable = true };
        var d = D(Absent, installing, route: Remote);
        Assert.Equal("Downloading…", d.Server.Text);
        Assert.Equal(SideCells.KindBusy, d.Server.Kind);
        Assert.Equal(B(SideButton.CancelInstall), d.Server.Buttons);

        var removing = new SideHalf { Busy = "Removing…", BusyCancelable = true, BusyIsRemoval = true };
        Assert.Equal(B(SideButton.CancelUninstall), D(Absent, removing, route: Remote).Server.Buttons);

        var late = new SideHalf { Busy = "Installing…", BusyCancelable = false };
        Assert.Empty(D(late).Client.Buttons);
    }

    [Fact]
    public void A_refused_install_says_so_without_a_button()
    {
        var refused = new SideHalf { State = HalfState.Absent, Refused = SideCells.CannotInstall };
        var d = D(refused);
        Assert.Equal(SideCells.CannotInstall, d.Client.Text);
        Assert.Equal(SideCellTone.Warn, d.Client.Tone);
        Assert.Empty(d.Client.Buttons);
    }

    [Fact]
    public void Tools_and_the_loader_are_never_installable()
    {
        var d = SideCells.Decide(new SideCellInputs
        {
            NotInstallable = true,
            NotInstallableText = "Tool, not a plugin",
            Target = ServerTargetKind.Server,
            Route = Local,
        });
        Assert.Equal("Tool, not a plugin", d.Client.Text);
        Assert.Empty(d.Client.Buttons);
        Assert.Equal(SideCells.Dash, d.Server.Text);
        Assert.Empty(d.Server.Buttons);
        Assert.Equal(SideButton.None, d.Shortcut);
    }

    [Fact]
    public void Every_cell_offers_at_most_two_buttons()
    {
        foreach (var c in new[] { Absent, Installed("1"), Pending(), Removal() })
        foreach (var s in new[] { Absent, Installed("1"), Pending(), Removal() })
        foreach (var t in new[] { ServerTargetKind.None, ServerTargetKind.Server, ServerTargetKind.PrivateGames })
        foreach (var r in new[] { Local, Remote, NotAdmin, NoServerRoute, Unreachable })
        foreach (var side in new[] { PackageSide.Both, PackageSide.Client, PackageSide.Server, PackageSide.Unknown })
        foreach (bool both in new[] { false, true })
        {
            var d = D(c, s, t, r, side, both, latest: "2");
            Assert.InRange(d.Client.Buttons.Count, 0, 2);
            Assert.InRange(d.Server.Buttons.Count, 0, 2);
            Assert.False(string.IsNullOrEmpty(d.Client.Text));
            Assert.False(string.IsNullOrEmpty(d.Server.Text));
            if (t != ServerTargetKind.Server) Assert.Empty(d.Server.Buttons);
            if (d.Shortcut != SideButton.None)
            {
                Assert.Contains(d.Shortcut, d.Client.Buttons);
                Assert.Contains(d.Shortcut, d.Server.Buttons);
            }
        }
    }

    [Fact]
    public void Captions_are_verb_first_and_never_a_bare_cancel()
    {
        foreach (var b in new[] { SideButton.Install, SideButton.Update, SideButton.Uninstall, SideButton.CancelInstall, SideButton.CancelUninstall })
        {
            string caption = SideCells.Caption(b);
            Assert.NotEqual("Cancel", caption);
            Assert.True(char.IsUpper(caption[0]));
            Assert.DoesNotContain("·", caption);
        }
        Assert.Equal("", SideCells.Caption(SideButton.None));
    }

    [Fact]
    public void Readback_names_every_field()
    {
        var w = SideCells.ToWidget(D(Installed("1.0.3"), latest: "1.1.0").Client);
        Assert.Equal(SideCells.KindUpdate, w["kind"]);
        Assert.Equal("warn", w["tone"]);
        Assert.Equal(new List<string> { "update", "uninstall" }, w["buttons"]);
    }

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.True(SideCells.Outdated("1.9.0", "1.10.0"));
        Assert.False(SideCells.Outdated("1.10.0", "1.9.0"));
        Assert.False(SideCells.Outdated("", "1.0.0"));
        Assert.False(SideCells.Outdated("1.0.0", ""));
        Assert.False(SideCells.Outdated("1.0.0", "1.0.0"));
    }

    // ---- INSTALLSIDES2 -------------------------------------------------------------------------

    [Fact]
    public void A_side_the_mod_does_not_use_is_grayed_and_says_so()
    {
        var server = D(Absent, side: PackageSide.Server);
        Assert.Equal(SideCells.NoClientSide, server.Client.Text);
        Assert.Equal("No client side", server.Client.Text);
        Assert.True(server.Client.Grayed);
        Assert.Empty(server.Client.Buttons);
        Assert.False(server.Server.Grayed);

        var client = D(Absent, side: PackageSide.Client);
        Assert.True(client.Server.Grayed);
        Assert.Equal(SideCells.KindNotNeeded, client.Server.Kind);
        Assert.False(client.Client.Grayed);
    }

    [Fact]
    public void Glyphs_mark_installed_and_update_and_nothing_else()
    {
        Assert.Equal(SideCells.InstalledGlyph, D(Installed("1.0.0"), Installed("1.0.0")).Client.Glyph);
        Assert.Equal("\u2713", SideCells.InstalledGlyph);
        Assert.Equal("\u2191", SideCells.UpdateGlyph);
        Assert.Equal(SideCells.UpdateGlyph, D(Installed("0.9.0"), latest: "1.0.0").Client.Glyph);
        Assert.Equal(SideCells.UpdateGlyph, D(Absent, Installed("1.0.0"), needsBoth: true).Client.Glyph);
        Assert.Equal("", D(Pending()).Client.Glyph);
        Assert.Equal("", D(Absent).Client.Glyph);
        // A copy another tool manages is muted, without the installed mark.
        Assert.Equal("", D(Installed("1.0.0", uninstall: false, managed: "Managed elsewhere")).Client.Glyph);
    }

    [Fact]
    public void Not_an_admin_keeps_the_server_states_and_locks_the_buttons()
    {
        // Absent on the server: an admin would get Install; this player gets one dim Admin only.
        var d = D(Absent, Absent, route: NotAdmin, admin: AdminOffer.AdminOnly);
        Assert.Equal(SideCells.NotInstalled, d.Server.Text);
        Assert.False(d.Server.Grayed);
        Assert.Equal(B(SideButton.AdminOnly), d.Server.Buttons);
        Assert.Equal(SideCells.AdminOnlyCaption, SideCells.Caption(SideButton.AdminOnly));
        Assert.Equal(SideButton.None, d.Shortcut);
        // Installed and behind: the update words stay readable.
        var u = D(Installed("1.0.0"), Installed("0.9.0"), route: NotAdmin, latest: "1.0.0", admin: AdminOffer.AdminOnly);
        Assert.Equal("0.9.0 · update 1.0.0", u.Server.Text);
        Assert.Equal(B(SideButton.AdminOnly), u.Server.Buttons);
        // On the admin list, not authenticated: the existing Authenticate as admin flow.
        var a = D(Absent, Absent, route: NotAdmin, admin: AdminOffer.Authenticate);
        Assert.Equal(B(SideButton.Authenticate), a.Server.Buttons);
        Assert.Equal(AdminAuthFlow.ButtonCaption, SideCells.Caption(SideButton.Authenticate));
        // A side the mod does not use has nothing to lock.
        var none = D(Absent, Absent, route: NotAdmin, side: PackageSide.Client, admin: AdminOffer.AdminOnly);
        Assert.Empty(none.Server.Buttons);
        Assert.True(none.Server.Grayed);
        // Without an offer (another block) the old note stands.
        Assert.Contains(SideCells.AskServerAdmin, D(Absent, Absent, route: NotAdmin).Server.Text);
    }

    [Theory]
    [InlineData((int)PackageSide.Client, (int)ServerTargetKind.Server, (int)ServerRouteKind.Local, true, "Client: loads without a restart")]
    [InlineData((int)PackageSide.Client, (int)ServerTargetKind.Server, (int)ServerRouteKind.Local, false, "Client: loads at the next game restart")]
    [InlineData((int)PackageSide.Server, (int)ServerTargetKind.Server, (int)ServerRouteKind.Local, true, "Server: loads at the next server restart")]
    [InlineData((int)PackageSide.Server, (int)ServerTargetKind.Server, (int)ServerRouteKind.Remote, true, "Server: loads without a restart")]
    [InlineData((int)PackageSide.Both, (int)ServerTargetKind.Server, (int)ServerRouteKind.Local, true, "Client: loads without a restart · Server: loads at the next server restart")]
    [InlineData((int)PackageSide.Both, (int)ServerTargetKind.None, (int)ServerRouteKind.None, false, "Client: loads at the next game restart")]
    [InlineData((int)PackageSide.Server, (int)ServerTargetKind.None, (int)ServerRouteKind.None, false, "")]
    [InlineData((int)PackageSide.Server, (int)ServerTargetKind.PrivateGames, (int)ServerRouteKind.None, false, "Private games: load the client's copy at their next start")]
    public void The_restart_note_has_one_part_per_used_side(int side, int target, int route, bool noRestart, string expected)
    {
        Assert.Equal(expected, SideCells.RestartNote((PackageSide)side, (ServerTargetKind)target, (ServerRouteKind)route, noRestart));
    }

    [Fact]
    public void The_restart_note_prefers_the_open_plan_and_is_empty_for_a_tool()
    {
        Assert.Equal("Client: loads without a restart", SideCells.RestartNote(PackageSide.Client, ServerTargetKind.Server, ServerRouteKind.Local, false, clientPlanNeedsRestart: false));
        Assert.Equal("", SideCells.RestartNote(PackageSide.Client, ServerTargetKind.Server, ServerRouteKind.Local, true, notInstallable: true));
    }
}
