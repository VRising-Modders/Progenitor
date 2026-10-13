using System;
using System.Collections.Generic;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>REMOTEINST1 per-side status: the decision, the source priority and the labels.</summary>
public sealed class SideStatusTests
{
    private static readonly ServerRoute Local = new(ServerRouteKind.Local, ServerRouteBlock.None);
    private static readonly ServerRoute Remote = new(ServerRouteKind.Remote, ServerRouteBlock.None);
    private static readonly ServerRoute NotAdmin = new(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
    private static readonly ServerRoute NoServer = new(ServerRouteKind.None, ServerRouteBlock.NoServer);

    private static SideDecision D(ClientHalfState c, ServerHalfState s, ServerRoute route, bool needsBoth = true,
        string cv = "1.0.0", string sv = "1.0.0") =>
        SideStatusRules.Decide(new SideInputs(c, cv, s, sv, ServerHalfSource.Local, needsBoth, route));

    public static IEnumerable<object[]> Matrix()
    {
        // client, server, route, label, action, note
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.Absent, "local", SideStatusRules.MissingOnServer, SideAction.InstallOnServer, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.Absent, "remote", SideStatusRules.MissingOnServer, SideAction.InstallOnServer, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.Absent, "notadmin", SideStatusRules.MissingOnServer, SideAction.None, SideStatusRules.AskServerAdmin };
        yield return new object[] { ClientHalfState.PendingRestart, ServerHalfState.Absent, "noserver", SideStatusRules.MissingOnServer, SideAction.None, SideStatusRules.NoServerToInstallTo };
        yield return new object[] { ClientHalfState.Absent, ServerHalfState.Installed, "remote", SideStatusRules.MissingOnClient, SideAction.InstallOnClient, "" };
        yield return new object[] { ClientHalfState.Absent, ServerHalfState.PendingRestart, "notadmin", SideStatusRules.MissingOnClient, SideAction.InstallOnClient, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.Installed, "local", SideStatusRules.Installed, SideAction.None, "" };
        yield return new object[] { ClientHalfState.PendingRestart, ServerHalfState.PendingRestart, "local", SideStatusRules.BothPendingRestart, SideAction.None, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.PendingRestart, "remote", InstallStatusLabels.PendingServerRestart, SideAction.None, "" };
        yield return new object[] { ClientHalfState.PendingRestart, ServerHalfState.Installed, "remote", InstallStatusLabels.PendingGameRestart, SideAction.None, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.Unknown, "noserver", SideStatusRules.ServerUnknown, SideAction.None, "" };
        yield return new object[] { ClientHalfState.RemovalPending, ServerHalfState.Installed, "local", InstallStatusLabels.RemovalPendingGameRestart, SideAction.None, "" };
        yield return new object[] { ClientHalfState.Installed, ServerHalfState.RemovalPending, "local", InstallStatusLabels.RemovalPendingServerRestart, SideAction.None, "" };
        yield return new object[] { ClientHalfState.Absent, ServerHalfState.Absent, "local", "", SideAction.None, "" };
    }

    private static ServerRoute RouteOf(string name) => name switch
    {
        "local" => Local,
        "remote" => Remote,
        "notadmin" => NotAdmin,
        _ => NoServer,
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void The_decision_matrix(object client, object server, string route, string label, object action, string note)
    {
        var d = D((ClientHalfState)client, (ServerHalfState)server, RouteOf(route));
        Assert.Equal(label, d.Label);
        Assert.Equal((SideAction)action, d.Action);
        Assert.Equal(note, d.Note);
        Assert.Equal(label == SideStatusRules.ServerUnknown, d.Muted);
    }

    [Fact]
    public void Without_a_declaration_nothing_is_ever_missing()
    {
        foreach (ClientHalfState c in Enum.GetValues(typeof(ClientHalfState)))
            foreach (ServerHalfState s in Enum.GetValues(typeof(ServerHalfState)))
                Assert.False(D(c, s, Remote, needsBoth: false).Decided);
    }

    [Fact]
    public void A_version_mismatch_updates_the_older_side()
    {
        var serverOlder = D(ClientHalfState.Installed, ServerHalfState.Installed, Remote, cv: "1.2.0", sv: "1.10.0".Replace("1.10.0", "1.1.9"));
        Assert.Equal(SideStatusRules.VersionMismatch, serverOlder.Label);
        Assert.Equal(SideAction.UpdateServer, serverOlder.Action);
        Assert.True(serverOlder.Mismatch);
        var clientOlder = D(ClientHalfState.Installed, ServerHalfState.Installed, Remote, cv: "1.2.0", sv: "1.10.0");
        Assert.Equal(SideAction.UpdateClient, clientOlder.Action);
        var noRoute = D(ClientHalfState.Installed, ServerHalfState.Installed, NotAdmin, cv: "1.2.0", sv: "1.1.0");
        Assert.Equal(SideAction.None, noRoute.Action);
        Assert.Equal(SideStatusRules.AskServerAdmin, noRoute.Note);
    }

    [Fact]
    public void The_server_half_comes_from_the_sources_in_priority_order()
    {
        var row = new RemoteServerRow("team-Sample", "2.0.0", RemoteRowState.Pending, ServerInstallLedger.RouteRemote);
        // (a) the local folder, when its route applies.
        var a = SideStatusRules.ServerHalf(Local, true, (ServerHalfState.Installed, "1.0.0"), row, true, "3.0.0", true);
        Assert.Equal((ServerHalfState.Installed, "1.0.0", ServerHalfSource.Local), a);
        // ...or a local server is configured and no remote route is in use.
        var a2 = SideStatusRules.ServerHalf(NoServer, true, (ServerHalfState.Absent, ""), null, false, "3.0.0", true);
        Assert.Equal(ServerHalfSource.Local, a2.Source);
        // (b) the remote rows, which include pending.
        var b = SideStatusRules.ServerHalf(Remote, true, (ServerHalfState.Installed, "1.0.0"), row, true, "3.0.0", true);
        Assert.Equal((ServerHalfState.PendingRestart, "2.0.0", ServerHalfSource.Remote), b);
        var bAbsent = SideStatusRules.ServerHalf(Remote, false, null, null, true, "3.0.0", true);
        Assert.Equal((ServerHalfState.Absent, "", ServerHalfSource.Remote), bAbsent);
        // (c) the handshake list, loaded mods only.
        var c = SideStatusRules.ServerHalf(NotAdmin, false, null, null, false, "3.0.0", true);
        Assert.Equal((ServerHalfState.Installed, "3.0.0", ServerHalfSource.Handshake), c);
        var cAbsent = SideStatusRules.ServerHalf(NotAdmin, false, null, null, false, null, true);
        Assert.Equal(ServerHalfState.Absent, cAbsent.State);
        // (d) unknown.
        var d = SideStatusRules.ServerHalf(NoServer, false, null, null, false, null, false);
        Assert.Equal((ServerHalfState.Unknown, "", ServerHalfSource.Unknown), d);
    }

    [Fact]
    public void The_captions_fit_the_status_button()
    {
        foreach (var action in new[] { SideAction.InstallOnServer, SideAction.InstallOnClient, SideAction.UpdateServer, SideAction.UpdateClient })
        {
            string caption = SideStatusRules.Caption(action);
            // INSTALLSIDES1: drawn as a side button at the side buttons' face, in a slot its own width.
            var spec = new Progenitor.Ui.CellSpec(Progenitor.Ui.CellKind.Button, "b").With(text: caption, fontSize: Progenitor.Ui.InstallColumns.SideButtonFontSize);
            Assert.True(Progenitor.Ui.CellLayout.CaptionFits(spec, Progenitor.Ui.InstallColumns.SideButtonWidth(caption)), caption);
        }
    }
}
