using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REMOTEFOLLOW1 (live 2026-10-03, after PR #129): the remote-route follow-ups — the admin lock on
/// installed rows, the status re-ask that lets an authorization change re-decide the route, no
/// remote Install the server is known to refuse, and the CastleLink column offset.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class RemoteFollowupsTests
{
    private static SideCellInputs ServerInputs(SideHalf server, AdminOffer admin = AdminOffer.AdminOnly) => new()
    {
        Side = PackageSide.Both,
        NeedsBoth = true,
        Latest = "0.1.0",
        Client = SideHalf.None,
        Server = server,
        Target = ServerTargetKind.Server,
        Route = new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin, "", forcedRemote: true),
        Admin = admin,
    };

    // ---- 1. Admin only on installed rows ---------------------------------------------------------

    [Theory]
    [InlineData((int)HalfState.Installed)]
    [InlineData((int)HalfState.Pending)]
    [InlineData((int)HalfState.RemovalPending)]
    public void A_held_server_copy_a_non_admin_cannot_manage_shows_the_lock(int state)
    {
        // The handshake view a non-admin gets: installed, no ledger rows, so no Uninstall of its own.
        var half = new SideHalf { State = (HalfState)state, Version = "0.1.0" };
        var cell = SideCells.Decide(ServerInputs(half)).Server;
        Assert.Equal(new[] { SideButton.AdminOnly }, cell.Buttons.ToArray());

        var auth = SideCells.Decide(ServerInputs(half, AdminOffer.Authenticate)).Server;
        Assert.Equal(new[] { SideButton.Authenticate }, auth.Buttons.ToArray());
        // Revert: returning the button-less cell reads "Installed 0.1.0" with nothing at all (live).
    }

    [Fact]
    public void A_copy_another_tool_manages_gets_no_lock()
    {
        var half = new SideHalf { State = HalfState.Installed, Version = "0.1.0", Managed = "Managed by Gale" };
        Assert.Empty(SideCells.Decide(ServerInputs(half)).Server.Buttons);
    }

    // ---- 2. authorization refresh ----------------------------------------------------------------

    [Fact]
    public void An_open_tab_re_asks_for_status_on_a_short_beat_while_authorization_blocks()
    {
        var client = RemoteInstallClient.Shared;
        client.ResetForTests();
        try
        {
            int sent = 0;
            client.Send = _ => { sent++; return true; };
            client.Accept(new RemoteInstallStatus(0, true, false, false, RemoteReachability.Reachable, "", false, null));
            var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(client.RequestStatusIfDue(t0, authorizationBlocked: true));
            Assert.False(client.RequestStatusIfDue(t0.AddSeconds(1), true));
            Assert.True(client.RequestStatusIfDue(t0 + RemoteInstallClient.BlockedRefresh, true));
            Assert.Equal(2, sent);

            // Not blocked: the long beat.
            var t1 = t0 + RemoteInstallClient.BlockedRefresh;
            Assert.False(client.RequestStatusIfDue(t1 + RemoteInstallClient.BlockedRefresh, false));
            Assert.True(client.RequestStatusIfDue(t1 + RemoteInstallClient.IdleRefresh, false));
            Assert.Equal(3, sent);
        }
        finally
        {
            client.ResetForTests();
        }
    }

    [Fact]
    public void A_new_status_re_decides_the_route()
    {
        var facts = new ServerRouteFacts
        {
            ConfiguredPath = @"C:\GameModding\_testserver\server", PathValid = true, FolderPort = 9976, Connected = true,
            ConnectedAddress = "127.0.0.1", ConnectedPort = 9976, IsLocalMachine = true, HandshakeOk = true, TreatLocalAsRemote = true,
            RemoteStatus = new RemoteInstallStatus(0, true, false, false, RemoteReachability.Reachable, "", false, null),
        };
        Assert.Equal(ServerRouteBlock.NotAdmin, ServerRouteSelector.Decide(facts).Route.Block);
        var granted = new ServerRouteFacts
        {
            ConfiguredPath = facts.ConfiguredPath, PathValid = true, FolderPort = 9976, Connected = true,
            ConnectedAddress = "127.0.0.1", ConnectedPort = 9976, IsLocalMachine = true, HandshakeOk = true, TreatLocalAsRemote = true,
            RemoteStatus = new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, Array.Empty<RemoteServerRow>()),
        };
        var route = ServerRouteSelector.Decide(granted).Route;
        Assert.Equal(ServerRouteKind.Remote, route.Kind);
        Assert.Equal(ServerRouteBlock.None, route.Block);
    }

    // ---- 3. no Install the server is known to refuse -------------------------------------------

    [Theory]
    [InlineData((int)RemoteInstallReason.NotThunderstore, true)]
    [InlineData((int)RemoteInstallReason.NotFound, true)]
    [InlineData((int)RemoteInstallReason.VersionNotFound, true)]
    [InlineData((int)RemoteInstallReason.NotVRising, true)]
    [InlineData((int)RemoteInstallReason.ThunderstoreUnreachable, false)]
    [InlineData((int)RemoteInstallReason.DownloadFailed, false)]
    [InlineData((int)RemoteInstallReason.Busy, false)]
    [InlineData((int)RemoteInstallReason.RateLimited, false)]
    public void A_refusal_the_server_would_repeat_offers_no_install(int reason, bool repeats)
    {
        Assert.Equal(repeats, InstallStatusLabels.WouldRepeat((RemoteInstallReason)reason));
        var half = new SideHalf
        {
            State = HalfState.Absent,
            Refused = InstallStatusLabels.Failure(new RemoteInstallProgress(1, RemoteInstallOp.Install, RemoteInstallStage.Refused,
                (RemoteInstallReason)reason, "o-mod", "1.0.0", "")),
            RefusedRetry = !repeats,
            RefusedQuiet = repeats,
        };
        var inputs = new SideCellInputs
        {
            Side = PackageSide.Both, NeedsBoth = true, Latest = "1.0.0", Client = SideHalf.None, Server = half,
            Target = ServerTargetKind.Server, Route = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None), Admin = AdminOffer.None,
        };
        var cell = SideCells.Decide(inputs).Server;
        Assert.Equal(SideCells.KindRefused, cell.Kind);
        Assert.Equal(!repeats, cell.Has(SideButton.Install));
        Assert.Equal(repeats ? SideCellTone.Muted : SideCellTone.Warn, cell.Tone);
    }

    // ---- 4. the column offset -------------------------------------------------------------------

    [Theory]
    [InlineData((int)SideButton.AdminOnly, (int)SideButton.None)]
    [InlineData((int)SideButton.Authenticate, (int)SideButton.None)]
    [InlineData((int)SideButton.Install, (int)SideButton.None)]
    [InlineData((int)SideButton.Update, (int)SideButton.Uninstall)]
    [InlineData((int)SideButton.CancelInstall, (int)SideButton.None)]
    [InlineData((int)SideButton.None, (int)SideButton.None)]
    public void A_side_cell_always_spans_exactly_its_column(int a, int b)
    {
        var first = (SideButton)a;
        var second = (SideButton)b;
        string ca = first == SideButton.None ? "" : SideCells.Caption(first);
        string cb = second == SideButton.None ? "" : SideCells.Caption(second);
        foreach (float column in new[] { 380f, 440f, 520f })
        {
            float text = InstallModsPane.SideTextWidthFor(column, ca, first, cb, second);
            float total = text;
            if (ca.Length > 0) total += Progenitor.Ui.CellLayout.Gap + InstallModsPane.SideButtonCellWidth(ca, first);
            if (cb.Length > 0) total += Progenitor.Ui.CellLayout.Gap + InstallModsPane.SideButtonCellWidth(cb, second);
            Assert.Equal(column, total, 2);
        }
        // Revert: the Admin only width with the lock's room, but the words' width without it, makes
        // that row ~25 px wider than the column (CastleLink, live).
    }
}
