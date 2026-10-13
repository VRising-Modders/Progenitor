using System;
using System.Collections.Generic;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>REMOTEINST1: the "Authenticate as admin" offer, its flag and its readback.</summary>
public sealed class AdminAuthFlowTests : IDisposable
{
    public AdminAuthFlowTests() => AdminAuthFlow.Shared.ResetForTests();

    public void Dispose()
    {
        AdminAuthFlow.Shared.ResetForTests();
        RemoteInstallBridgeOps.IsServer = null;
        RemoteInstallBridgeOps.ClientRoute = null;
    }

    private static RemoteInstallStatus Status(bool onList, bool authed) =>
        new(0, true, onList || authed, onList || authed, RemoteReachability.Reachable, "", false, null,
            authorizedBy: onList ? RemoteAuthorizedBy.AdminList : authed ? RemoteAuthorizedBy.AdminAuth : RemoteAuthorizedBy.None,
            onAdminList: onList, adminAuthed: authed);

    [Theory]
    // onList, authed, gated, flag, expected
    [InlineData(true, false, true, true, AdminAuthOfferKind.Button)]
    [InlineData(true, false, true, false, AdminAuthOfferKind.TypeCommand)]
    [InlineData(true, true, true, true, AdminAuthOfferKind.None)]
    [InlineData(false, true, true, true, AdminAuthOfferKind.None)]
    [InlineData(false, false, true, true, AdminAuthOfferKind.AskHost)]
    [InlineData(false, false, true, false, AdminAuthOfferKind.AskHost)]
    [InlineData(true, false, false, true, AdminAuthOfferKind.None)]
    [InlineData(false, false, false, true, AdminAuthOfferKind.None)]
    public void The_visibility_matrix(bool onList, bool authed, bool gated, bool flag, object expected) =>
        Assert.Equal((AdminAuthOfferKind)expected, AdminAuthFlow.Offer(Status(onList, authed), flag, gated));

    [Fact]
    public void No_word_from_the_server_offers_nothing() =>
        Assert.Equal(AdminAuthOfferKind.None, AdminAuthFlow.Offer(null, true, true));

    [Fact]
    public void The_flag_gates_the_game_call()
    {
        int ran = 0;
        var flow = AdminAuthFlow.Shared;
        flow.Executor = () => { ran++; return true; };
        Assert.False(flow.Begin());
        Assert.Equal(0, ran);
        Assert.Equal(AdminAuthFlow.TypeCommandHelp, flow.Message);

        flow.Enabled = true;
        int asked = 0;
        flow.RequestStatus = () => { asked++; return true; };
        Assert.True(flow.Begin());
        Assert.Equal(1, ran);
        Assert.Equal(1, asked);
        Assert.Equal(AdminAuthState.Authenticating, flow.State);
        Assert.Equal(AdminAuthFlow.AuthenticatingText, flow.Message);
    }

    [Fact]
    public void The_next_status_settles_the_attempt()
    {
        var flow = AdminAuthFlow.Shared;
        double now = 0;
        flow.Now = () => now;
        flow.Enabled = true;
        flow.Executor = () => true;
        flow.Begin();
        flow.Observe(Status(onList: true, authed: false));
        Assert.Equal(AdminAuthState.Authenticating, flow.State);
        flow.Observe(Status(onList: true, authed: true));
        Assert.Equal(AdminAuthState.Done, flow.State);

        flow.Begin();
        now = AdminAuthFlow.TimeoutSeconds + 1;
        flow.Observe(Status(onList: true, authed: false));
        Assert.Equal(AdminAuthState.Failed, flow.State);
        Assert.Equal(AdminAuthFlow.FailedNoAnswer, flow.Message);

        flow.Executor = () => false;
        Assert.False(flow.Begin());
        Assert.Equal(AdminAuthFlow.FailedNoCommand, flow.Message);
    }

    [Fact]
    public void The_status_carries_list_membership_and_adminauth()
    {
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(Status(onList: true, authed: false)), out var back));
        Assert.True(back.OnAdminList);
        Assert.False(back.AdminAuthed);
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(Status(onList: false, authed: true)), out back));
        Assert.False(back.OnAdminList);
        Assert.True(back.AdminAuthed);
    }

    [Fact]
    public void The_bridge_reads_back_and_drives_the_same_path()
    {
        RemoteInstallBridgeOps.IsServer = () => false;
        var flow = AdminAuthFlow.Shared;
        int ran = 0;
        flow.Executor = () => { ran++; return true; };
        var off = RemoteInstallBridgeOps.Run("adminauth", "", "", false, "");
        Assert.Equal(false, off["ok"]);
        Assert.Equal(0, ran);
        flow.Enabled = true;
        var on = RemoteInstallBridgeOps.Run("adminauth", "", "", false, "");
        Assert.Equal(true, on["ok"]);
        Assert.Equal(1, ran);
        var auth = (Dictionary<string, object?>)on["adminAuth"]!;
        Assert.Equal(true, auth["buttonEnabled"]);
        Assert.Equal("authenticating", auth["state"]);
    }
}
