using System;
using System.IO;
using Progenitor.Bootstrap;
using Progenitor.Materials;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// A reserved event id with no handler is consumed, not handed to the game. The trap it closes: a client whose
/// Bootstrap lacked the material seams never registered <c>'PrMv'</c>, the server sent Build, Craft and Upgrade
/// views to every online player anyway, and the game's event reader rejected each one as malformed and logged a
/// line roughly every three seconds. Three layers now stop it: the receive route drops any reserved id without a
/// handler (logged once per id), the client owns <c>'PrMv'</c> whether or not its seams bound, and the server
/// sends views only to a client that declared <see cref="Handshake.Capability.MaterialViews"/>.
/// </summary>
public class ReservedEventConsumeTests
{
    private const int MaterialView = 0x50724D76;   // 'PrMv'
    private const int OtherReserved = 0x5072Aa01;  // reserved range, never assigned
    private const int Vanilla = 0x1A2B3C4D;        // not in the reserved range

    private static readonly EventIdOwner Core = EventIdOwner.ForCore("Progenitor");

    [Fact]
    public void The_material_view_id_is_in_the_reserved_range()
    {
        Assert.Equal(MaterialView, MaterialViewWire.EventId);
        Assert.True(CustomEventPolicy.IsReserved(MaterialView));
        Assert.Equal("'PrMv'", CustomEventPolicy.FourCc(MaterialView));
    }

    [Fact]
    public void A_reserved_id_with_no_handler_is_consumed_and_logged_once()
    {
        var gate = new ReservedDropGate();
        var table = new EventHandlerTable<Action>("client", gate);

        Assert.Equal(ReceiveRoute.DropReserved, table.Route(MaterialView, out var handler, out var first));
        Assert.Null(handler);
        Assert.NotNull(first);
        Assert.Contains("'PrMv'", first);
        Assert.Contains("0x50724D76", first);
        Assert.Contains("dropped", first);
        Assert.Contains("client", first);

        // A server resending every few seconds: still consumed, never logged again.
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(ReceiveRoute.DropReserved, table.Route(MaterialView, out _, out var again));
            Assert.Null(again);
        }

        // Another reserved id gets its own single line.
        Assert.Equal(ReceiveRoute.DropReserved, table.Route(OtherReserved, out _, out var other));
        Assert.NotNull(other);
    }

    [Fact]
    public void The_server_table_drops_reserved_ids_the_same_way()
    {
        var table = new EventHandlerTable<Action>("server", new ReservedDropGate());
        Assert.Equal(ReceiveRoute.DropReserved, table.Route(MaterialView, out _, out var log));
        Assert.Contains("server", log);
    }

    [Fact]
    public void A_non_reserved_unknown_id_still_passes_to_the_game()
    {
        var gate = new ReservedDropGate();
        var table = new EventHandlerTable<Action>("client", gate);

        Assert.Equal(ReceiveRoute.PassToGame, table.Route(Vanilla, out var handler, out var log));
        Assert.Null(handler);
        Assert.Null(log);
        Assert.Equal(ReceiveRoute.PassToGame, CustomEventPolicy.RouteReceive(hasHandler: false, Vanilla));
    }

    [Fact]
    public void A_registered_id_is_dispatched_reserved_or_not()
    {
        var table = new EventHandlerTable<Action>("client", new ReservedDropGate());
        Action h = () => { };
        Assert.Equal(EventRegistrationResult.Stored, table.Register(MaterialView, h, Core, out _));

        Assert.Equal(ReceiveRoute.Dispatch, table.Route(MaterialView, out var got, out var log));
        Assert.Same(h, got);
        Assert.Null(log);
        Assert.Equal(ReceiveRoute.Dispatch, CustomEventPolicy.RouteReceive(hasHandler: true, Vanilla));
    }

    [Fact]
    public void The_log_gate_is_bounded()
    {
        var gate = new ReservedDropGate();
        for (int i = 0; i < ReservedDropGate.MaxIds; i++)
            Assert.True(gate.Note(0x50720000 | i));
        Assert.False(gate.Note(0x5072FFFF));
        Assert.False(gate.Note(0x50720000));
        gate.ForgetLogged();
        Assert.True(gate.Note(0x5072FFFF));
    }

    /// <summary>
    /// The Core-swap freeze is not affected: while frozen Bootstrap never calls Core's dispatcher, so the new
    /// route cannot swallow a packet there. An owned id is buffered for replay and an unowned one goes to the game
    /// exactly as before.
    /// </summary>
    [Fact]
    public void The_freeze_path_never_reaches_the_core_route()
    {
        bool called = false;
        var action = ReceiveSlotPolicy.Invoke(() => { called = true; return true; }, isEventsPacket: true,
            ReceiveSlotPolicy.HandshakeRequest, frozen: true, out bool threw);
        Assert.False(called);
        Assert.False(threw);
        Assert.Equal(ReceiveAction.Buffered, action);
    }

    [Fact]
    public void Both_dispatchers_use_the_table_route()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "CustomEvents.cs");
        string client = SourcePin.MethodBody(source, "private static bool TryDispatchClient(");
        string server = SourcePin.MethodBody(source, "private static bool TryDispatchServer(");

        SourcePin.AssertUncommented(client, "route = ClientHandlers.Route(eventId, out handler, out dropLog);");
        SourcePin.AssertUncommented(server, "route = ServerHandlers.Route(eventId, out handler, out dropLog);");
        foreach (var body in new[] { client, server })
        {
            SourcePin.AssertStatement(body, "if (route == ReceiveRoute.PassToGame) return false;");
            SourcePin.AssertStatement(body, "if (route == ReceiveRoute.DropReserved || handler == null)");
        }
    }

    // ---- capability gating -------------------------------------------------------------------------------

    [Fact]
    public void Material_views_is_a_runtime_bit_on_a_free_value()
    {
        Assert.Equal(8u, (uint)Handshake.Capability.MaterialViews);
        Assert.Equal(Handshake.Capability.None, Handshake.SelfCapabilities & Handshake.Capability.MaterialViews);
        foreach (Handshake.Capability other in Enum.GetValues(typeof(Handshake.Capability)))
            if (other != Handshake.Capability.MaterialViews)
                Assert.Equal(0u, (uint)other & 8u);

        Assert.Equal(Handshake.Capability.MaterialViews, Handshake.RuntimeCapabilities(readsMaterialViews: true));
        Assert.Equal(Handshake.Capability.None, Handshake.RuntimeCapabilities(readsMaterialViews: false));

        // It survives the capability row's round trip.
        var caps = Handshake.SelfCapabilities | Handshake.RuntimeCapabilities(true);
        var row = Handshake.Compare.CreateCapabilityRecord(caps);
        Assert.Equal(caps, Handshake.Compare.ReadCapabilityBlob(row.Version));
    }

    [Fact]
    public void A_client_without_material_views_is_sent_no_view()
    {
        var oldOrUnbound = Handshake.SelfCapabilities; // every build bit, including CharacterCraft and RefineView
        Assert.False(MaterialClientPolicy.ReadsViews(oldOrUnbound));
        foreach (var op in MaterialClientPolicy.ViewOperations)
            Assert.False(MaterialClientPolicy.ReadByPeer(op, oldOrUnbound));
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.Build, Handshake.Capability.None));
    }

    [Fact]
    public void A_bound_client_gets_views_and_operation_bits_still_gate_their_own()
    {
        var bound = Handshake.SelfCapabilities | Handshake.Capability.MaterialViews;
        Assert.True(MaterialClientPolicy.ReadsViews(bound));
        foreach (var op in MaterialClientPolicy.ViewOperations)
            Assert.True(MaterialClientPolicy.ReadByPeer(op, bound));

        var viewsOnly = Handshake.Capability.MaterialViews;
        Assert.True(MaterialClientPolicy.ReadByPeer(MaterialOperation.Build, viewsOnly));
        Assert.True(MaterialClientPolicy.ReadByPeer(MaterialOperation.Craft, viewsOnly));
        Assert.True(MaterialClientPolicy.ReadByPeer(MaterialOperation.Upgrade, viewsOnly));
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.CharacterCraft, viewsOnly));
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.RefineView, viewsOnly));

        // The operation bits alone are not enough.
        var opsOnly = Handshake.Capability.MaterialCharacterCraft | Handshake.Capability.MaterialRefineView;
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.CharacterCraft, opsOnly));
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.RefineView, opsOnly));
    }

    [Fact]
    public void The_server_send_loop_uses_the_capability_decision()
    {
        string body = SourcePin.MethodBody(MaterialSeamSource(), "private static bool PushPlayers(");
        SourcePin.AssertStatement(body, "var caps = Handshake.CapsOf(userIndex);");
        SourcePin.AssertStatementExactly(body, "if (!MaterialClientPolicy.ReadsViews(caps)) continue;");
        SourcePin.AssertStatementExactly(body, "if (MaterialClientPolicy.ReadByPeer(op, caps))");
    }

    [Fact]
    public void The_client_answer_carries_the_runtime_bit()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        SourcePin.AssertUncommented(source, "| RuntimeCapabilities(ClientReadsMaterialViews)");
    }

    // ---- the client handler while unbound ----------------------------------------------------------------

    [Fact]
    public void The_client_registers_the_view_handler_bound_or_not()
    {
        Assert.True(MaterialClientPolicy.RegistersViewHandler(server: false));
        Assert.False(MaterialClientPolicy.RegistersViewHandler(server: true));
        Assert.False(MaterialClientPolicy.AppliesView(seamsBound: false));
        Assert.True(MaterialClientPolicy.AppliesView(seamsBound: true));
    }

    [Fact]
    public void The_view_handler_is_registered_before_the_unbound_return()
    {
        string install = SourcePin.MethodBody(MaterialSeamSource(), "internal static void Install(");
        int register = install.IndexOf("CustomEvents.OnReceive(MaterialViewWire.EventId, OnView);", StringComparison.Ordinal);
        int declare = install.IndexOf("Handshake.ClientReadsMaterialViews = _bound;", StringComparison.Ordinal);
        int unbound = install.IndexOf("if (!_bound) return;", StringComparison.Ordinal);
        Assert.True(register >= 0 && unbound >= 0 && register < unbound,
            "the client must own 'PrMv' before Install returns early on unbound seams");
        Assert.True(declare >= 0 && declare < unbound, "the runtime capability must be set before the early return");
        Assert.Equal(-1, install.IndexOf("CustomEvents.OnReceive(MaterialViewWire.EventId, OnView);", register + 1, StringComparison.Ordinal));

        string onView = SourcePin.MethodBody(MaterialSeamSource(), "private static void OnView(");
        SourcePin.AssertStatementExactly(onView, "if (!MaterialClientPolicy.AppliesView(_bound)) return;");

        string uninstall = SourcePin.MethodBody(MaterialSeamSource(), "internal static void Uninstall(");
        SourcePin.AssertStatement(uninstall, "Handshake.ClientReadsMaterialViews = false;");
    }

    private static string MaterialSeamSource() =>
        File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Materials", "MaterialSeamRuntime.cs"));
}
