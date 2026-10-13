using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>PrfE</c> wire (job FEDW1, design <c>docs/edits-on-the-wire.md</c>): the server is
/// authoritative for edited component values and ships them, so a client with a different editor
/// — or no editor at all — sees the server's numbers.
///
/// <para><b>What is testable here and what is not.</b> Framing, every cap, the dedupe, the build
/// rule and the type-name rules are pure and run exactly as they ship. The byte-level writer
/// (<c>Marshal.PtrToStructure</c> plus <c>Write&lt;T&gt;</c> closed by reflection) needs an
/// <c>EntityManager</c>, so it is verified by <c>PrefabRegistry.DebugRoundTripEditLayer</c> on the
/// live server instead; what this file pins about the client half is the part that decides
/// <em>when</em> the layer is written — after every local pass — and how it interacts with the
/// baselines, using the same <see cref="PrefabEditPass.Run"/> ladder the registry drives.</para>
/// </summary>
public class PrefabEditWireTests
{
    private const int GuidHash = 42;
    private const int BuildHash = 0x1234_5678;

    // ---- framing ---------------------------------------------------------------------------

    [Fact]
    public void A_packet_round_trips_every_field()
    {
        var records = new List<PrefabEditComponentBytes>
        {
            Rec("ProjectM.ItemData", 1, 2, 3, 4),
            Rec("ProjectM.Shared.BuffModificationFlagData", 7, 0, 0, 0, 9),
        };

        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 99, records, out var packet, out var error));
        Assert.Equal("", error);

        Assert.True(PrefabEditWire.TryRead(
            packet, out int guid, out int build, out int identity, out var back, out error));
        Assert.Equal("", error);
        Assert.Equal(GuidHash, guid);
        Assert.Equal(BuildHash, build);
        Assert.Equal(99, identity);
        Assert.Equal(2, back.Count);
        Assert.Equal("ProjectM.ItemData", back[0].TypeFullName);
        Assert.Equal("ItemData", back[0].ShortName);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, back[0].Bytes);
        Assert.Equal("ProjectM.Shared.BuffModificationFlagData", back[1].TypeFullName);
        Assert.Equal(new byte[] { 7, 0, 0, 0, 9 }, back[1].Bytes);
    }

    [Fact]
    public void The_header_is_thirteen_bytes_and_the_event_id_is_PrfE()
    {
        Assert.Equal(13, PrefabEditWire.HeaderBytes);
        Assert.Equal(0x50726645, PrefabEditWire.EventId);
        // One id past PrfD (0x50726644), which is unsafe and so not compiled into this suite.
        Assert.Equal(0x50726644 + 1, PrefabEditWire.EventId);

        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, new[] { Rec("A.B", 1) }, out var packet, out _));
        Assert.Equal(PrefabEditWire.HeaderBytes + 1 + 3 + 2 + 1, packet.Length);
    }

    [Fact]
    public void A_payload_of_more_than_255_bytes_survives_the_two_byte_length()
    {
        var big = new byte[1024];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);
        var records = new[] { new PrefabEditComponentBytes("ProjectM.Big", big) };

        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, records, out var packet, out _));
        Assert.True(PrefabEditWire.TryRead(packet, out _, out _, out _, out var back, out _));
        Assert.Equal(big, back[0].Bytes);
    }

    // ---- caps ------------------------------------------------------------------------------

    [Fact]
    public void The_count_cap_refuses_the_seventeenth_component()
    {
        var candidates = new List<PrefabEditComponentBytes>();
        for (int i = 0; i < PrefabEditWire.MaxEditComponents + 3; i++)
            candidates.Add(Rec("ProjectM.C" + i, 1, 2, 3, 4));

        var accepted = PrefabEditWire.SelectRecords(candidates, out var refusals);

        Assert.Equal(PrefabEditWire.MaxEditComponents, accepted.Count);
        Assert.Equal(3, refusals.Count);
        Assert.All(refusals, r => Assert.Contains("more than 16 component(s) touched", r));
        Assert.False(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, candidates, out _, out var error));
        Assert.Contains("exceeds 16", error);
    }

    [Fact]
    public void A_type_name_that_is_too_long_or_not_ascii_is_refused()
    {
        string tooLong = new('x', PrefabEditWire.MaxTypeNameBytes + 1);
        string notAscii = "ProjectM.Ünicode";

        var accepted = PrefabEditWire.SelectRecords(
            new[] { Rec(tooLong, 1), Rec(notAscii, 1), Rec("", 1), Rec("ProjectM.Ok", 1) },
            out var refusals);

        Assert.Single(accepted);
        Assert.Equal("ProjectM.Ok", accepted[0].TypeFullName);
        Assert.Equal(3, refusals.Count);
        Assert.Contains("type name exceeds 127 ASCII bytes", refusals[0]);
        Assert.Contains("type name is not ASCII", refusals[1]);
        Assert.Contains("type name is empty", refusals[2]);

        Assert.False(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, new[] { Rec(tooLong, 1) }, out _, out var error));
        Assert.Contains("type name exceeds", error);
    }

    [Fact]
    public void An_empty_or_oversized_payload_is_refused()
    {
        var empty = new PrefabEditComponentBytes("ProjectM.Empty", Array.Empty<byte>());
        var oversized = new PrefabEditComponentBytes("ProjectM.Fat", new byte[PrefabEditWire.MaxComponentBytes + 1]);

        var accepted = PrefabEditWire.SelectRecords(new[] { empty, oversized }, out var refusals);

        Assert.Empty(accepted);
        Assert.Equal(2, refusals.Count);
        Assert.Contains("payload is empty", refusals[0]);
        Assert.Contains("payload of 1025 bytes exceeds 1024", refusals[1]);

        Assert.False(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, new[] { oversized }, out _, out var error));
        Assert.Contains("max is 1024", error);
    }

    /// <summary>
    /// The per-record caps multiply out past the transport's 15 KiB payload, so the selector — not
    /// the send — is what keeps a packet sendable. Sixteen full-size records do not fit; the ones
    /// that do not are refused by name rather than truncating the packet.
    /// </summary>
    [Fact]
    public void The_total_packet_cap_trims_before_the_transport_ever_sees_it()
    {
        var candidates = new List<PrefabEditComponentBytes>();
        for (int i = 0; i < PrefabEditWire.MaxEditComponents; i++)
            candidates.Add(new PrefabEditComponentBytes("ProjectM.Fat" + i, new byte[PrefabEditWire.MaxComponentBytes]));

        var accepted = PrefabEditWire.SelectRecords(candidates, out var refusals);

        Assert.True(accepted.Count < PrefabEditWire.MaxEditComponents);
        Assert.NotEmpty(refusals);
        Assert.All(refusals, r => Assert.Contains("packet would exceed 15360 bytes", r));
        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, accepted, out var packet, out _));
        Assert.True(packet.Length <= PrefabEditWire.MaxPacketBytes);
    }

    [Fact]
    public void An_empty_record_set_builds_nothing()
    {
        Assert.False(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, Array.Empty<PrefabEditComponentBytes>(), out _, out var error));
        Assert.Equal("no components to send", error);
    }

    // ---- hostile packets ---------------------------------------------------------------------

    [Fact]
    public void A_short_packet_is_refused()
    {
        Assert.False(PrefabEditWire.TryRead(new byte[5], out _, out _, out _, out _, out var error));
        Assert.Contains("shorter than the 13-byte header", error);
    }

    [Fact]
    public void A_count_of_zero_is_a_withdraw_and_more_than_the_cap_is_refused()
    {
        // count 0 used to be "refused, count is 0"; FEDW3 spends the encoding on the withdraw a
        // layer on a vanilla prefab had no other way of getting (R51 finding 7).
        var withdraw = PrefabEditWire.WriteWithdraw(GuidHash, BuildHash, 7);
        Assert.Equal(PrefabEditWire.HeaderBytes, withdraw.Length);
        Assert.True(PrefabEditWire.TryRead(
            withdraw, out int guid, out int build, out int identity, out var records, out var error));
        Assert.Equal("", error);
        Assert.Equal(GuidHash, guid);
        Assert.Equal(BuildHash, build);
        Assert.Equal(7, identity);
        Assert.Empty(records);

        // A withdraw carries nothing, so anything after the header refuses the packet.
        var trailing = new byte[PrefabEditWire.HeaderBytes + 1];
        Array.Copy(withdraw, trailing, withdraw.Length);
        Assert.False(PrefabEditWire.TryRead(trailing, out _, out _, out _, out _, out error));
        Assert.Contains("trailing byte(s) after a withdraw", error);

        // The builder still refuses to produce one by accident: an empty layer is not a withdraw.
        Assert.False(PrefabEditWire.TryWrite(
            GuidHash, BuildHash, 0, Array.Empty<PrefabEditComponentBytes>(), out _, out error));
        Assert.Equal("no components to send", error);

        var tooMany = new byte[PrefabEditWire.HeaderBytes];
        tooMany[12] = PrefabEditWire.MaxEditComponents + 1;
        Assert.False(PrefabEditWire.TryRead(tooMany, out _, out _, out _, out _, out error));
        Assert.Contains("exceeds 16", error);
    }

    [Fact]
    public void A_truncated_record_is_refused_rather_than_read_past_the_end()
    {
        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, new[] { Rec("ProjectM.ItemData", 1, 2, 3, 4) },
            out var packet, out _));

        for (int cut = 1; cut < packet.Length - PrefabEditWire.HeaderBytes; cut++)
        {
            var shortened = new byte[packet.Length - cut];
            Array.Copy(packet, shortened, shortened.Length);
            Assert.False(PrefabEditWire.TryRead(shortened, out _, out _, out _, out _, out string error));
            Assert.NotEqual("", error);
        }
    }

    [Fact]
    public void Trailing_bytes_refuse_the_whole_packet()
    {
        Assert.True(PrefabEditWire.TryWrite(GuidHash, BuildHash, 0, new[] { Rec("ProjectM.ItemData", 1, 2, 3, 4) },
            out var packet, out _));
        var padded = new byte[packet.Length + 3];
        Array.Copy(packet, padded, packet.Length);

        Assert.False(PrefabEditWire.TryRead(padded, out _, out _, out _, out var records, out var error));
        Assert.Equal("3 trailing byte(s)", error);
        Assert.Empty(records);
    }

    [Fact]
    public void A_packet_larger_than_the_cap_is_refused_before_it_is_walked()
    {
        Assert.False(PrefabEditWire.TryRead(
            new byte[PrefabEditWire.MaxPacketBytes + 1], out _, out _, out _, out _, out var error));
        Assert.Contains("exceeds 15360", error);
    }

    // ---- the build rule -----------------------------------------------------------------------

    [Fact]
    public void An_unknown_build_hashes_to_zero_and_zero_matches_only_zero()
    {
        Assert.Equal(0, PrefabEditWire.Fnv1a(null));
        Assert.Equal(0, PrefabEditWire.Fnv1a(""));
        int known = PrefabEditWire.Fnv1a("1.2.3.456789");
        Assert.NotEqual(0, known);

        Assert.True(PrefabEditWire.BuildMatches(0, 0));
        Assert.False(PrefabEditWire.BuildMatches(0, known));
        Assert.False(PrefabEditWire.BuildMatches(known, 0));
        Assert.True(PrefabEditWire.BuildMatches(known, PrefabEditWire.Fnv1a("1.2.3.456789")));
        Assert.False(PrefabEditWire.BuildMatches(known, PrefabEditWire.Fnv1a("1.2.3.456790")));
    }

    [Fact]
    public void The_build_mismatch_line_names_both_sides_once()
    {
        string line = PrefabEditLog.EditLayerBuildMismatch(
            GuidHash, 0x11112222, 0x33334444, "vrising/1.1.13.0+r99712+b17/net4321");

        Assert.Contains("PrfE build mismatch on guid 42", line);
        Assert.Contains("0x11112222", line);
        Assert.Contains("0x33334444", line);
        Assert.Contains("vrising/1.1.13.0+r99712+b17/net4321", line);
        Assert.Contains("no game identity detected", PrefabEditLog.EditLayerBuildMismatch(GuidHash, 1, 0, null));
    }

    // ---- receiver-side type rules ----------------------------------------------------------------

    [Theory]
    [InlineData("ProjectM", true)]
    [InlineData("ProjectM.Shared", true)]
    [InlineData("Stunlock.Core", true)]
    [InlineData("Unity.Entities", true)]
    [InlineData("UnityEngine.CoreModule", true)]
    [InlineData("Progenitor", false)]
    [InlineData("mscorlib", false)]
    [InlineData("System.Private.CoreLib", false)]
    [InlineData("Il2CppInterop.Runtime", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("projectm", false)]
    public void Only_ProjectM_Stunlock_and_Unity_assemblies_may_define_a_wire_type(string? assembly, bool allowed)
    {
        Assert.Equal(allowed, PrefabEditWire.IsAllowedAssemblyName(assembly));
    }

    [Theory]
    [InlineData("Stunlock.Core.PrefabGUID", true)]
    [InlineData("Unity.Entities.Prefab", true)]
    [InlineData("PrefabGUID", true)]
    [InlineData("Prefab", true)]
    [InlineData("ProjectM.ItemData", false)]
    [InlineData("ProjectM.PrefabGUIDHolder", false)]
    public void Identity_components_are_refused_by_name_whatever_namespace_claims_them(string name, bool refused)
    {
        Assert.Equal(refused, PrefabEditWire.IsRefusedTypeName(name, out string reason));
        if (refused) Assert.Equal("identity component", reason);
    }

    [Theory]
    [InlineData("ProjectM.ItemData", "ItemData")]
    [InlineData("ItemData", "ItemData")]
    [InlineData("A.B.C.D", "D")]
    [InlineData("", "")]
    public void The_short_name_is_what_the_baselines_store_is_keyed_by(string full, string expected)
    {
        Assert.Equal(expected, PrefabEditWire.ShortTypeName(full));
    }

    [Fact]
    public void A_size_that_disagrees_with_the_receivers_own_marshal_size_is_refused()
    {
        Assert.True(PrefabEditWire.SizeMatches(24, 24));
        Assert.False(PrefabEditWire.SizeMatches(23, 24));
        Assert.False(PrefabEditWire.SizeMatches(25, 24));
        // A type Marshal.SizeOf could not measure (0) never matches, whatever the packet says.
        Assert.False(PrefabEditWire.SizeMatches(0, 0));
    }

    // ---- dedupe ------------------------------------------------------------------------------

    [Fact]
    public void Unchanged_bytes_send_nothing_and_a_change_sends_again()
    {
        var layer = new PrefabEditWireLayer();
        var first = new[] { Rec("ProjectM.ItemData", 1, 2, 3, 4) };
        var same = new[] { Rec("ProjectM.ItemData", 1, 2, 3, 4) };
        var changed = new[] { Rec("ProjectM.ItemData", 1, 2, 3, 5) };

        Assert.Equal(PrefabEditWire.PayloadHash(first), PrefabEditWire.PayloadHash(same));
        Assert.NotEqual(PrefabEditWire.PayloadHash(first), PrefabEditWire.PayloadHash(changed));

        Assert.True(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(first)));
        Assert.False(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(same)));
        Assert.True(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(changed)));
        Assert.False(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(changed)));

        // A join needs the layer even though nothing changed.
        Assert.True(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(changed), force: true));

        // A send that reached nobody must not leave a hash behind.
        layer.ForgetSent(GuidHash);
        Assert.True(layer.ShouldSend(GuidHash, PrefabEditWire.PayloadHash(changed)));
    }

    [Fact]
    public void The_payload_hash_covers_the_type_names_and_the_order()
    {
        var a = new[] { Rec("ProjectM.A", 1), Rec("ProjectM.B", 2) };
        var swapped = new[] { Rec("ProjectM.B", 2), Rec("ProjectM.A", 1) };
        var renamed = new[] { Rec("ProjectM.A", 1), Rec("ProjectM.C", 2) };
        // Same concatenated bytes, different split between name and payload.
        var shifted = new[] { Rec("ProjectM.A", 1, 2) };

        Assert.NotEqual(PrefabEditWire.PayloadHash(a), PrefabEditWire.PayloadHash(swapped));
        Assert.NotEqual(PrefabEditWire.PayloadHash(a), PrefabEditWire.PayloadHash(renamed));
        Assert.NotEqual(PrefabEditWire.PayloadHash(a), PrefabEditWire.PayloadHash(shifted));
        Assert.Equal(PrefabEditWire.PayloadHash(null), PrefabEditWire.PayloadHash(Array.Empty<PrefabEditComponentBytes>()));
    }

    // ---- readers ------------------------------------------------------------------------------

    [Fact]
    public void A_component_gets_one_reader_and_the_serialised_order_is_registration_order()
    {
        var layer = new PrefabEditWireLayer();
        int calls = 0;

        Assert.True(layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => { calls++; return new byte[] { 1 }; }));
        Assert.False(layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => new byte[] { 2 }));
        // Both names are on Core's wire-safe list, which is what a reader now requires
        // (job FEDW4, R52 finding 1; PrefabEditWireTypesTests drives the list itself).
        Assert.True(layer.RegisterReader(GuidHash, "ProjectM.LifeTime", _ => new byte[] { 3 }));

        Assert.Equal(new[] { "ProjectM.ItemData", "ProjectM.LifeTime" }, layer.ReaderTypes(GuidHash));
        var records = layer.Serialize(GuidHash, new object());
        Assert.Equal(new[] { "ProjectM.ItemData", "ProjectM.LifeTime" },
            new[] { records[0].TypeFullName, records[1].TypeFullName });
        Assert.Equal(new byte[] { 1 }, records[0].Bytes);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void An_identity_component_never_gets_a_reader()
    {
        var layer = new PrefabEditWireLayer();

        Assert.False(layer.RegisterReader(GuidHash, "Stunlock.Core.PrefabGUID", _ => new byte[] { 1 }));
        Assert.False(layer.RegisterReader(GuidHash, "Unity.Entities.Prefab", _ => new byte[] { 1 }));
        Assert.False(layer.RegisterReader(GuidHash, "", _ => new byte[] { 1 }));
        Assert.False(layer.RegisterReader(GuidHash, "ProjectM.ItemData", null));

        Assert.Empty(layer.ReaderTypes(GuidHash));
    }

    [Fact]
    public void A_reader_that_throws_or_answers_nothing_is_skipped_and_the_rest_still_serialise()
    {
        var layer = new PrefabEditWireLayer();
        // Vouched names throughout: an unvouched one gets no reader at all now (job FEDW4).
        layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => throw new InvalidOperationException("boom"));
        layer.RegisterReader(GuidHash, "ProjectM.DisableAggroBuff", _ => null);
        layer.RegisterReader(GuidHash, "ProjectM.LifeTime", _ => new byte[] { 9 });

        var errors = new List<string>();
        var records = layer.Serialize(GuidHash, new object(), (type, e) => errors.Add(type + ":" + e.Message));

        Assert.Single(records);
        Assert.Equal("ProjectM.LifeTime", records[0].TypeFullName);
        Assert.Equal(new[] { "ProjectM.ItemData:boom" }, errors);
    }

    // ---- lifetime -------------------------------------------------------------------------------

    [Fact]
    public void Dropping_a_prefab_drops_its_readers_and_its_sent_hash_but_not_the_server_layer()
    {
        var layer = new PrefabEditWireLayer();
        layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => new byte[] { 1 });
        layer.ShouldSend(GuidHash, 7);
        layer.SetReceived(GuidHash, new[] { Rec("ProjectM.ItemData", 1) });

        layer.DropGuid(GuidHash);

        Assert.Empty(layer.ReaderTypes(GuidHash));
        Assert.True(layer.ShouldSend(GuidHash, 7));
        Assert.True(layer.HasReceived(GuidHash));

        layer.DropReceived(GuidHash);
        Assert.False(layer.HasReceived(GuidHash));
        Assert.Empty(layer.Received(GuidHash));
    }

    [Fact]
    public void A_core_swap_forgets_this_generations_readers_and_keeps_the_servers_layer()
    {
        var layer = new PrefabEditWireLayer();
        layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => new byte[] { 1 });
        layer.ShouldSend(GuidHash, 7);
        layer.SetReceived(GuidHash, new[] { Rec("ProjectM.ItemData", 5) });

        layer.ClearOwned();

        Assert.Empty(layer.ReaderTypes(GuidHash));
        Assert.True(layer.ShouldSend(GuidHash, 7));
        Assert.Equal(new byte[] { 5 }, layer.Received(GuidHash)[0].Bytes);

        // A new client world is the other direction: the server's state goes, ours stays.
        layer.RegisterReader(GuidHash, "ProjectM.ItemData", _ => new byte[] { 1 });
        layer.ClearReceived();
        Assert.False(layer.HasReceived(GuidHash));
        Assert.Single(layer.ReaderTypes(GuidHash));

        layer.Clear();
        Assert.Empty(layer.ReaderTypes(GuidHash));
        Assert.Empty(layer.ReceivedGuids());
    }

    [Fact]
    public void One_prefab_is_charged_against_the_join_budget_once_per_join()
    {
        var layer = new PrefabEditWireLayer();

        Assert.True(layer.NeedsBudgetCharge(GuidHash));
        Assert.False(layer.NeedsBudgetCharge(GuidHash));
        Assert.True(layer.NeedsBudgetCharge(GuidHash + 1));

        // A refused charge is released so a later join can try again.
        layer.ReleaseBudgetCharge(GuidHash);
        Assert.True(layer.NeedsBudgetCharge(GuidHash));

        layer.ResetBudgetCharges();
        Assert.True(layer.NeedsBudgetCharge(GuidHash));
    }

    // ---- the client half, through the production pass ladder ---------------------------------------

    /// <summary>
    /// The design's central claim: a client-side editor cannot win. The local pass runs and writes
    /// 7; the server layer says 99; after the pass the entity carries 99. The re-apply is driven
    /// from the same place the registry drives it — after a pass that ran editors.
    /// </summary>
    [Fact]
    public void The_server_layer_is_re_applied_after_every_local_pass()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = new PrefabEditTable();
        Add(table, "a", h => h.Write("ItemData", 7));
        table.Wire.SetReceived(GuidHash, new[] { Int("ProjectM.ItemData", 99) });

        var outcome = RunPass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, outcome);
        Assert.Equal(99, world.Values["ItemData"]);

        // And again on the next pass, after the editor has written 7 over it a second time.
        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, RunPass(table, world, PrefabEditTrigger.EditorsChanged));
        Assert.Equal(99, world.Values["ItemData"]);
    }

    /// <summary>
    /// The layer is captured as a baseline like any other write, so the removal of the last editor
    /// (which is what a <c>PrfU</c> unapply and a plugin unload both reach) puts vanilla back —
    /// not the server's value, and not the local editor's.
    /// </summary>
    [Fact]
    public void Removing_the_last_editor_restores_vanilla_through_the_layers_baseline()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = new PrefabEditTable();
        var handle = Add(table, "a", h => h.Write("ItemData", 7));
        table.Wire.SetReceived(GuidHash, new[] { Int("ProjectM.ItemData", 99) });

        RunPass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(99, world.Values["ItemData"]);
        Assert.Equal(new[] { "ItemData" }, table.Baselines.Components(GuidHash));

        handle.Dispose();
        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        var outcome = RunPass(table, world, PrefabEditTrigger.EditorsChanged);

        Assert.Equal(PrefabEditPassOutcome.Restored, outcome);
        Assert.Equal(1, world.Values["ItemData"]);
    }

    /// <summary>
    /// A client with no editor at all — the server-only balance mod the design is for. There is no
    /// pass to ride, so the layer is written on receipt and the value the player sees is the
    /// server's; the baseline it captured is still vanilla.
    /// </summary>
    [Fact]
    public void A_client_with_no_editor_still_gets_the_servers_value_and_can_still_be_restored()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = new PrefabEditTable();
        table.Wire.SetReceived(GuidHash, new[] { Int("ProjectM.ItemData", 99) });

        var applied = ApplyLayer(table, world);

        Assert.Equal(new[] { "ItemData" }, applied);
        Assert.Equal(99, world.Values["ItemData"]);
        Assert.Equal(new[] { "ItemData" },
            table.Baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(1, world.Values["ItemData"]);
    }

    [Fact]
    public void The_applied_line_is_the_one_the_two_process_check_greps()
    {
        Assert.Equal(
            "PrefabRegistry: applied PrfE for 480926629: ItemData (server layer)",
            PrefabEditLog.AppliedEditLayer(480926629, new[] { "ItemData" }));
        Assert.Contains("nothing (server layer)", PrefabEditLog.AppliedEditLayer(1, Array.Empty<string>()));
    }

    [Fact]
    public void Every_refusal_names_the_record_and_the_reason()
    {
        Assert.Equal(
            "PrefabRegistry: refused PrfE record ProjectM.ItemData for guid 42: not a value type",
            PrefabEditLog.EditLayerRecordRefused(GuidHash, "ProjectM.ItemData", "not a value type"));
        Assert.Contains("(unnamed)", PrefabEditLog.EditLayerRecordRefused(GuidHash, "", "type name is empty"));
        Assert.Equal(
            "PrefabRegistry: sent PrfE for 42: 1 component(s) to 2 user(s): ProjectM.ItemData",
            PrefabEditLog.SentEditLayer(GuidHash, new[] { "ProjectM.ItemData" }, 2));
        Assert.Contains("dropped a PrfE packet: 3 trailing byte(s)", PrefabEditLog.EditLayerDropped("3 trailing byte(s)"));
    }

    // ---- bounds on what a server may push (job FEDW3, R51 finding 2) ---------------------------

    [Fact]
    public void A_type_name_with_a_control_byte_is_refused_by_the_parser()
    {
        // Hand-frame a packet whose name carries a newline. TryWrite cannot produce one — the send
        // side has always promised ASCII — and before FEDW3 TryRead masked with & 0x7F and kept
        // it, so the name reached sixteen log sites per packet and could forge whole lines.
        var name = Encoding.ASCII.GetBytes("A\nPrefabRegistry: applied PrfE for 1: ItemData");
        var packet = new byte[PrefabEditWire.HeaderBytes + 1 + name.Length + 2 + 4];
        packet[12] = 1;
        int o = PrefabEditWire.HeaderBytes;
        packet[o++] = (byte)name.Length;
        Array.Copy(name, 0, packet, o, name.Length);
        o += name.Length;
        packet[o++] = 4;
        packet[o] = 0;

        Assert.False(PrefabEditWire.TryRead(packet, out _, out _, out _, out var records, out var error));
        Assert.Contains("outside printable ASCII", error);
        Assert.Empty(records);
    }

    [Fact]
    public void A_name_that_reaches_a_log_line_is_sanitised_and_the_line_says_so()
    {
        string hostile = "X\r\nPrefabRegistry: applied PrfE for 1: ItemData (server layer)";
        string safe = PrefabEditWire.SanitiseTypeName(hostile, out bool altered);
        Assert.True(altered);
        Assert.DoesNotContain("\n", safe);
        Assert.DoesNotContain("\r", safe);
        Assert.True(safe.Length <= PrefabEditWire.MaxLoggedTypeNameChars);

        string line = PrefabEditLog.EditLayerRecordRefused(GuidHash, hostile, "not a value type");
        Assert.DoesNotContain("\n", line);
        Assert.Contains("name sanitized for this log", line);

        // An honest name is printed as it is.
        Assert.Equal(
            "ProjectM.ItemData",
            PrefabEditWire.SanitiseTypeName("ProjectM.ItemData", out bool honest));
        Assert.False(honest);
    }

    [Fact]
    public void The_per_join_PrfE_budget_bounds_packets_and_bytes_and_logs_once()
    {
        int packetCap = PrefabSyncBudget.EditPacketCap;
        int byteCap = PrefabSyncBudget.EditByteCap;
        try
        {
            PrefabSyncBudget.EditPacketCap = 3;
            PrefabSyncBudget.EditByteCap = 1_000_000;
            PrefabSyncBudget.BeginJoin(out _);
            Assert.True(PrefabSyncBudget.TryAdmitEditPacket(10, out var log));
            Assert.Null(log);
            Assert.True(PrefabSyncBudget.TryAdmitEditPacket(10, out _));
            Assert.True(PrefabSyncBudget.TryAdmitEditPacket(10, out _));
            Assert.False(PrefabSyncBudget.TryAdmitEditPacket(10, out log));
            Assert.Contains("exceeded the per-join PrfE budget", log);
            Assert.False(PrefabSyncBudget.TryAdmitEditPacket(10, out log));
            Assert.Null(log);            // one line per join, not one per packet
            Assert.Equal(3, PrefabSyncBudget.EditPackets);
            Assert.Equal(30, PrefabSyncBudget.EditBytes);

            // Bytes bound it independently of the packet count.
            PrefabSyncBudget.EditPacketCap = 1000;
            PrefabSyncBudget.EditByteCap = 50;
            PrefabSyncBudget.BeginJoin(out _);
            Assert.True(PrefabSyncBudget.TryAdmitEditPacket(40, out _));
            Assert.False(PrefabSyncBudget.TryAdmitEditPacket(40, out _));
            Assert.Equal(1, PrefabSyncBudget.EditPackets);
        }
        finally
        {
            PrefabSyncBudget.EditPacketCap = packetCap;
            PrefabSyncBudget.EditByteCap = byteCap;
            PrefabSyncBudget.Reset();
        }
    }

    [Fact]
    public void An_identical_repeat_for_the_same_prefab_is_applied_once()
    {
        var layer = new PrefabEditWireLayer();
        var records = new[] { Int("ProjectM.ItemData", 99) };
        int hash = PrefabEditWire.PayloadHash(records);

        Assert.True(layer.ShouldApplyReceived(GuidHash, hash));
        Assert.False(layer.ShouldApplyReceived(GuidHash, hash));
        Assert.False(layer.ShouldApplyReceived(GuidHash, hash));

        // A real change is not deduped.
        int changed = PrefabEditWire.PayloadHash(new[] { Int("ProjectM.ItemData", 50) });
        Assert.True(layer.ShouldApplyReceived(GuidHash, changed));

        // A withdraw clears the fingerprint, so the same bytes coming back are applied again.
        layer.SetReceived(GuidHash, records);
        Assert.True(layer.DropReceived(GuidHash));
        Assert.True(layer.ShouldApplyReceived(GuidHash, changed));
    }

    [Fact]
    public void The_send_rate_floor_defers_and_the_trailing_send_is_owed_not_dropped()
    {
        var layer = new PrefabEditWireLayer();
        const int interval = PrefabEditWireLayer.MinSendIntervalMs;

        // Nothing sent yet: the first push goes.
        Assert.True(layer.TryTakeSendSlot(GuidHash, 1_000, interval));
        layer.RecordSent(GuidHash, 1, 1_000);

        // Inside the interval it is suppressed and owed.
        Assert.False(layer.TryTakeSendSlot(GuidHash, 1_500, interval));
        Assert.Equal(1, layer.DeferredCount);
        Assert.Empty(layer.DueDeferred(1_500, interval));

        // Once the interval has passed the trailing send is handed back.
        Assert.Equal(new[] { GuidHash }, layer.DueDeferred(2_000, interval));
        Assert.True(layer.TryTakeSendSlot(GuidHash, 2_000, interval));
        layer.RecordSent(GuidHash, 2, 2_000);
        Assert.Equal(0, layer.DeferredCount);
    }

    [Fact]
    public void The_content_dedupe_is_asked_before_the_build_and_records_only_a_real_send()
    {
        var layer = new PrefabEditWireLayer();
        int hash = PrefabEditWire.PayloadHash(new[] { Int("ProjectM.ItemData", 7) });

        // WouldSend has no side effect: asking twice answers the same, so a build that then fails
        // cannot leave the prefab deduped against a packet nobody received (R51 finding 8).
        Assert.True(layer.WouldSend(GuidHash, hash));
        Assert.True(layer.WouldSend(GuidHash, hash));
        Assert.False(layer.HasSent(GuidHash));

        layer.RecordSent(GuidHash, hash, 0);
        Assert.False(layer.WouldSend(GuidHash, hash));
        Assert.True(layer.HasSent(GuidHash));
    }

    [Fact]
    public void A_withdraw_is_claimed_once_so_two_seams_cannot_both_send_one()
    {
        var layer = new PrefabEditWireLayer();
        Assert.False(layer.TakeSentForWithdraw(GuidHash));   // nothing out on the wire
        layer.RecordSent(GuidHash, 1, 0);
        Assert.True(layer.TakeSentForWithdraw(GuidHash));
        Assert.False(layer.TakeSentForWithdraw(GuidHash));
        Assert.False(layer.HasSent(GuidHash));
    }

    // ---- one measure on the wire (R51 finding 4) ------------------------------------------------

    [Fact]
    public void Both_ends_take_the_marshalled_measure_and_a_bool_widens()
    {
        // The managed sizeof of this struct is 6; the marshaled layout widens each bool to four
        // bytes, so it is 12. FEDW1's typed reader emitted the managed layout while the receiver
        // validated the marshaled one, and every record of a bool-carrying struct — which this
        // game is full of — was refused as a size mismatch on its own server.
        var value = new BoolCarrier { A = true, N = 7, B = false };
        Assert.True(PrefabEditWire.TryMarshalBytes(value, typeof(BoolCarrier), out var bytes, out var error));
        Assert.Equal("", error);
        Assert.Equal(Marshal.SizeOf(typeof(BoolCarrier)), bytes.Length);
        Assert.True(bytes.Length > 6);

        // The size rule the receiver applies is that same number, so the round trip agrees.
        Assert.True(PrefabEditWire.SizeMatches(bytes.Length, Marshal.SizeOf(typeof(BoolCarrier))));
        Assert.False(PrefabEditWire.SizeMatches(6, Marshal.SizeOf(typeof(BoolCarrier))));
        Assert.Contains(
            "Marshal.SizeOf(ProjectM.Follower) is 32",
            PrefabEditWire.SizeMismatchReason(30, 32, "ProjectM.Follower"));
    }

    private struct BoolCarrier
    {
        public bool A;
        public int N;
        public bool B;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static PrefabEditComponentBytes Rec(string name, params byte[] bytes) => new(name, bytes);

    /// <summary>One 4-byte little-endian int, the value shape <see cref="FakeWorld"/> stores.</summary>
    private static PrefabEditComponentBytes Int(string name, int value) =>
        new(name, BitConverter.GetBytes(value));

    private static IDisposable Add(PrefabEditTable table, string name, Action<FakeHandle> editor)
    {
        Assert.True(table.TryAdd(
            GuidHash, name + ".id", name, (int)PrefabEditPriority.Default, null, null,
            owner: new object(), editor, loadOrder: -1, out var handle, out var refuse));
        Assert.Null(refuse);
        return handle;
    }

    /// <summary>
    /// One pass through the production ladder, with the client's wire step where the registry puts
    /// it: after a pass that ran editors, re-apply the stored server layer.
    /// </summary>
    private static PrefabEditPassOutcome RunPass(PrefabEditTable table, FakeWorld world, PrefabEditTrigger trigger)
    {
        var pass = new FakePass(table, world, trigger);
        var outcome = PrefabEditPass.Run(trigger, pass);
        if (outcome is PrefabEditPassOutcome.DroppedThenRan or PrefabEditPassOutcome.RestoredThenRan)
            ApplyLayer(table, world);
        return outcome;
    }

    /// <summary>
    /// The client's apply, through the <em>production</em> ladder
    /// (<see cref="PrefabEditApplyDecision.Apply"/>) with a fake site under it. FEDW1 drove a hand
    /// copy of <c>ApplyRecords</c> here that omitted the four gating checks — resolve, size,
    /// <c>HasComponent</c>, the per-record catch — which are exactly the security boundary
    /// (R51 finding 6). A double that cannot represent a branch cannot fail when it breaks.
    /// </summary>
    private static string[] ApplyLayer(PrefabEditTable table, FakeWorld world) =>
        PrefabEditApplyDecision.Apply(table.Wire.Received(GuidHash), new FakeSite(table, world));

    /// <summary>
    /// <see cref="IPrefabEditApplySite"/> over <see cref="FakeWorld"/>'s int bag: a component is a
    /// four-byte int, and the shape says nothing is preserved unless the test asks for it.
    /// </summary>
    private sealed class FakeSite : IPrefabEditApplySite
    {
        private readonly PrefabEditTable _table;
        private readonly FakeWorld _world;

        public FakeSite(PrefabEditTable table, FakeWorld world)
        {
            _table = table;
            _world = world;
        }

        public readonly List<string> Refusals = new();

        public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
        {
            string shortName = PrefabEditWire.ShortTypeName(typeFullName);
            shape = _world.Shapes.TryGetValue(shortName, out var s)
                ? s
                : new PrefabEditTypeShape(typeFullName ?? "", sizeof(int), Array.Empty<PrefabEditByteRange>(), Array.Empty<string>(), shortName);
            if (_world.Refused.Contains(shortName))
            {
                reason = "refused by the test";
                return false;
            }
            reason = "";
            return true;
        }

        public bool HasComponent(PrefabEditTypeShape shape, out string reason)
        {
            if (_world.Values.ContainsKey(shape.ShortName))
            {
                reason = "";
                return true;
            }
            reason = "the prefab entity does not carry it";
            return false;
        }

        public byte[]? CurrentBytes(PrefabEditTypeShape shape) =>
            _world.Values.TryGetValue(shape.ShortName, out int v) ? BitConverter.GetBytes(v) : null;

        public void CaptureBaseline(PrefabEditTypeShape shape, string shortName)
        {
            if (!_world.Values.TryGetValue(shortName, out int original)) return;
            if (!PrefabEditPass.NeedsCapture(_table.Baselines, GuidHash, shortName)) return;
            PrefabEditPass.TryCapture(
                _table.Baselines, GuidHash, shortName, true, _world.EntityIndex, _world.EntityVersion,
                ctx => ((FakeWorld)ctx).Values[shortName] = original);
        }

        public void Write(PrefabEditTypeShape shape, byte[] bytes)
        {
            if (_world.Throws.Contains(shape.ShortName))
                throw new InvalidOperationException("the test's writer refuses " + shape.ShortName);
            _world.Values[shape.ShortName] = BitConverter.ToInt32(bytes, 0);
        }
    }

    private sealed class FakeWorld
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);

        /// <summary>Short names the site refuses to resolve (the type-rule refusal).</summary>
        public readonly HashSet<string> Refused = new(StringComparer.Ordinal);

        /// <summary>Short names whose write throws (the per-record catch).</summary>
        public readonly HashSet<string> Throws = new(StringComparer.Ordinal);

        /// <summary>Shapes the test supplies, e.g. one with a preserved range.</summary>
        public readonly Dictionary<string, PrefabEditTypeShape> Shapes = new(StringComparer.Ordinal);

        public int EntityIndex = 7;
        public int EntityVersion = 1;
    }

    /// <summary>The component-bag half of the ladder; every decision above it is the shipped one.</summary>
    private sealed class FakePass : IPrefabEditPass
    {
        private readonly PrefabEditTable _table;
        private readonly FakeWorld _world;
        private readonly PrefabEditTrigger _trigger;
        private PrefabEditRegistration[] _ordered = Array.Empty<PrefabEditRegistration>();
        private FakeHandle? _handle;

        public FakePass(PrefabEditTable table, FakeWorld world, PrefabEditTrigger trigger)
        {
            _table = table;
            _world = world;
            _trigger = trigger;
        }

        public bool WorldAlive => true;

        public bool HasEditors => _table.HasGuid(GuidHash);

        public int BaselineCount => _table.Baselines.Count(GuidHash);

        public bool RestoreOnly()
        {
            Restore();
            _table.Baselines.Drop(GuidHash);
            _table.Wire.DropGuid(GuidHash);
            return true;
        }

        public bool TryBeginRun() => _table.TryBeginRun(GuidHash);

        public void EndRun() => _table.EndRun(GuidHash);

        public bool TrySnapshot(out int orderedCount)
        {
            if (!_table.TrySnapshotOrdered(GuidHash, out _ordered, out _))
            {
                _ordered = Array.Empty<PrefabEditRegistration>();
                orderedCount = 0;
                return false;
            }
            orderedCount = _ordered.Length;
            return true;
        }

        public bool ShouldRun() => _table.ShouldRun(GuidHash, _trigger, _ordered);

        public bool TryBind()
        {
            _handle = new FakeHandle(_world, _table, GuidHash);
            return true;
        }

        public void DropBaselines()
        {
            _table.Baselines.Drop(GuidHash);
            _table.Wire.DropGuid(GuidHash);
        }

        public void RestoreBeforeRerun() => Restore();

        public void RunEditors()
        {
            var handle = _handle!;
            var records = PrefabEditApply.Run(
                _ordered,
                reg =>
                {
                    handle.Begin();
                    ((Action<FakeHandle>)reg.Editor)(handle);
                    return handle.Touched;
                },
                _ => { },
                (_, _) => { });
            _table.RememberRun(GuidHash, _ordered, records);
        }

        private void Restore() =>
            _table.Baselines.Restore(GuidHash, _world.EntityIndex, _world.EntityVersion, _world);
    }

    /// <summary>
    /// The tracked write surface: capture the baseline, register the reader, write. The reader is
    /// registered at the write site because that is the only place the component type is known —
    /// the same reason the baseline callback is built there.
    /// </summary>
    private sealed class FakeHandle
    {
        private readonly FakeWorld _world;
        private readonly PrefabEditTable _table;
        private readonly int _guidHash;
        private readonly List<string> _touched = new();

        public FakeHandle(FakeWorld world, PrefabEditTable table, int guidHash)
        {
            _world = world;
            _table = table;
            _guidHash = guidHash;
        }

        public IReadOnlyList<string> Touched => _touched;

        public void Begin() => _touched.Clear();

        public int Read(string component) => _world.Values[component];

        public void Write(string component, int value)
        {
            if (PrefabEditPass.NeedsCapture(_table.Baselines, _guidHash, component))
            {
                bool present = _world.Values.TryGetValue(component, out int original);
                PrefabEditPass.TryCapture(
                    _table.Baselines, _guidHash, component, present,
                    _world.EntityIndex, _world.EntityVersion,
                    ctx => ((FakeWorld)ctx).Values[component] = original);
            }
            var world = _world;
            _table.Wire.RegisterReader(
                _guidHash,
                "ProjectM." + component,
                _ => BitConverter.GetBytes(world.Values[component]));
            _world.Values[component] = value;
            if (!_touched.Contains(component)) _touched.Add(component);
        }
    }

    /// <summary>
    /// The readers are registered by the write site, so a pass is also what fills the send side.
    /// This is the server half of the same fake: run a pass, serialize what it wrote.
    /// </summary>
    [Fact]
    public void A_pass_leaves_a_serialisable_layer_behind()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = new PrefabEditTable();
        Add(table, "a", h => h.Write("ItemData", 99));

        RunPass(table, world, PrefabEditTrigger.EditorsChanged);
        var records = table.Wire.Serialize(GuidHash, world);

        Assert.Single(records);
        Assert.Equal("ProjectM.ItemData", records[0].TypeFullName);
        Assert.Equal(99, BitConverter.ToInt32(records[0].Bytes, 0));

        Assert.True(PrefabEditWire.TryWrite(
            GuidHash, PrefabEditWire.Fnv1a("build"), PrefabEditWire.Fnv1a(table.Stamp(GuidHash)),
            records, out var packet, out _));
        Assert.True(PrefabEditWire.TryRead(packet, out _, out _, out int identity, out var back, out _));
        Assert.Equal(PrefabEditWire.Fnv1a(table.Stamp(GuidHash)), identity);
        Assert.Equal(99, BitConverter.ToInt32(back[0].Bytes, 0));
        Assert.NotEqual(0, identity);
        _ = Encoding.ASCII;
    }

    // ---- the shared game identity (job FEDW2, R50 finding 10) ----------------------------------

    // The version constants this build's two binaries carry, from the game's metadata for the
    // client and the server. Six fields agree; three (Name,
    // UnixTimestamp, Date) do not, because the two binaries were built about an hour apart.
    // The network version is one number by construction - the server disconnects a hail that
    // disagrees with its own (reason 0xd, internal research notes) - so a connected pair
    // always shares it.
    private const int DumpMajor = 1;
    private const int DumpMinor = 1;
    private const int DumpPatch = 13;
    private const int DumpRevision = 0;
    private const int DumpVcsRevision = 99712;
    private const int DumpBuild = 17;
    private const int DumpNetworkVersion = 4321;

    private static PrefabGameVersionFacts ClientFacts(int network = DumpNetworkVersion) =>
        new("VRising", DumpMajor, DumpMinor, DumpPatch, DumpRevision, DumpVcsRevision, DumpBuild,
            1779722780L, network);

    private static PrefabGameVersionFacts ServerFacts(int network = DumpNetworkVersion) =>
        new("VRisingServer", DumpMajor, DumpMinor, DumpPatch, DumpRevision, DumpVcsRevision, DumpBuild,
            1779728998L, network);

    /// <summary>
    /// The identity a client and a server of the same build compose is the same string and the
    /// same hash — the property the address-library fingerprint could never have, because it is a
    /// hash of the local <c>GameAssembly.dll</c> and the two files differ.
    /// </summary>
    [Fact]
    public void A_client_and_a_server_of_the_same_build_compose_the_same_identity()
    {
        string client = PrefabEditBuildIdentity.Compose(ClientFacts());
        string server = PrefabEditBuildIdentity.Compose(ServerFacts());

        Assert.Equal("vrising/1.1.13.0+r99712+b17/net4321", client);
        Assert.Equal(client, server);
        Assert.NotEqual(0, PrefabEditBuildIdentity.Hash(ClientFacts()));
        Assert.Equal(PrefabEditBuildIdentity.Hash(ClientFacts()), PrefabEditBuildIdentity.Hash(ServerFacts()));
        Assert.True(PrefabEditWire.BuildMatches(
            PrefabEditBuildIdentity.Hash(ClientFacts()), PrefabEditBuildIdentity.Hash(ServerFacts())));

        // The three fields that differ between the two binaries are the three it must not use.
        Assert.DoesNotContain("Server", client, StringComparison.Ordinal);
        Assert.DoesNotContain("1779722780", client, StringComparison.Ordinal);
        Assert.DoesNotContain("1779728998", server, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every field the identity does use changes it, so a patch, a rebuild of the same version, or
    /// a different network protocol number all refuse each other's bytes.
    /// </summary>
    [Fact]
    public void Every_field_the_identity_uses_changes_it()
    {
        string baseline = PrefabEditBuildIdentity.Compose(ClientFacts());

        Assert.NotEqual(baseline, PrefabEditBuildIdentity.Compose(
            new PrefabGameVersionFacts("VRising", 1, 1, 14, 0, DumpVcsRevision, DumpBuild, 0, DumpNetworkVersion)));
        Assert.NotEqual(baseline, PrefabEditBuildIdentity.Compose(
            new PrefabGameVersionFacts("VRising", 1, 1, 13, 1, DumpVcsRevision, DumpBuild, 0, DumpNetworkVersion)));
        Assert.NotEqual(baseline, PrefabEditBuildIdentity.Compose(
            new PrefabGameVersionFacts("VRising", 1, 1, 13, 0, 99713, DumpBuild, 0, DumpNetworkVersion)));
        Assert.NotEqual(baseline, PrefabEditBuildIdentity.Compose(
            new PrefabGameVersionFacts("VRising", 1, 1, 13, 0, DumpVcsRevision, 18, 0, DumpNetworkVersion)));
        Assert.NotEqual(baseline, PrefabEditBuildIdentity.Compose(ClientFacts(DumpNetworkVersion + 1)));

        Assert.False(PrefabEditWire.BuildMatches(
            PrefabEditBuildIdentity.Hash(ClientFacts()),
            PrefabEditBuildIdentity.Hash(ClientFacts(DumpNetworkVersion + 1))));
    }

    /// <summary>
    /// A process that could not read its version composes the unknown identity, which hashes to 0
    /// — and 0 still matches only 0, so it never accepts bytes laid out for a build it cannot name.
    /// </summary>
    [Fact]
    public void An_unreadable_version_is_the_unknown_identity_and_matches_only_itself()
    {
        var none = new PrefabGameVersionFacts("", 0, 0, 0, 0, 0, 0, 0, 0);
        var negative = new PrefabGameVersionFacts("VRising", 1, 1, 13, 0, -1, 17, 0, 4321);

        Assert.Equal(PrefabEditBuildIdentity.Unknown, PrefabEditBuildIdentity.Compose(none));
        Assert.Equal(PrefabEditBuildIdentity.Unknown, PrefabEditBuildIdentity.Compose(negative));
        Assert.Equal(0, PrefabEditBuildIdentity.Hash(none));
        Assert.True(PrefabEditWire.BuildMatches(0, PrefabEditBuildIdentity.Hash(none)));
        Assert.False(PrefabEditWire.BuildMatches(
            PrefabEditBuildIdentity.Hash(ClientFacts()), PrefabEditBuildIdentity.Hash(none)));
    }

    /// <summary>
    /// The pin: the per-binary derivation is <em>gone</em>. <c>AddressLibrary.DetectedBuild</c>
    /// differs between the client's and the server's <c>GameAssembly.dll</c>, so a rule built on
    /// it drops every packet in a two-process check; nothing in the <c>PrfE</c> path may read it
    /// again, and the identity must come from the version data both sides share.
    /// </summary>
    [Fact]
    public void The_prfE_identity_is_not_derived_from_the_per_binary_address_library()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
        SourcePin.AssertNoStatementContaining(source, "AddressLibrary");

        string identity = SourcePin.MethodBody(source, "internal static string LocalIdentity()");
        SourcePin.AssertStatement(identity, "var data = GameVersionUtils.GetVersionData();");
        SourcePin.AssertUncommented(identity, "GameVersionUtils.GetNetworkVersion()");
        SourcePin.AssertUncommented(identity, "PrefabEditBuildIdentity.Compose(facts)");

        // Expression-bodied, so the pin is over the declaration line itself.
        SourcePin.AssertStatementExactly(
            source, "internal static int LocalBuildHash() => PrefabEditWire.Fnv1a(LocalIdentity());");
    }

    // ---- what MakeGenericMethod does and does not enforce --------------------------------------

    /// <summary>A struct that is a value type and is <em>not</em> <c>unmanaged</c>.</summary>
    private struct StructWithManagedField
    {
        public string Text;
        public int Number;
    }

    /// <summary>A struct that is both.</summary>
    private struct BlittableStruct
    {
        public int A;
        public int B;
    }

    /// <summary>A struct whose managed and marshaled layouts differ, as this game's do.</summary>
    private struct StructWithBool
    {
        public bool Flag;
        public bool Other;
        public int Number;
    }

    private static void UnmanagedOnly<T>(T value) where T : unmanaged { _ = value; }

    /// <summary>
    /// Measured, not assumed (R50 finding 10): <c>MakeGenericMethod</c> enforces "value type",
    /// which reflection can see, and <b>not</b> <c>unmanaged</c>, which is a modreq it cannot. So
    /// closing <c>Write&lt;T&gt;</c> over a struct carrying a <c>string</c> succeeds, and the only
    /// thing standing between such a type and a memcpy into an ECS chunk is the wire's own check.
    /// </summary>
    [Fact]
    public void MakeGenericMethod_enforces_value_type_but_not_unmanaged()
    {
        var definition = typeof(PrefabEditWireTests).GetMethod(
            nameof(UnmanagedOnly),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(definition);

        // The constraint the runtime does check.
        Assert.ThrowsAny<ArgumentException>(() => definition!.MakeGenericMethod(typeof(string)));

        // The constraint it does not: this is the call that would corrupt a chunk.
        var closed = definition!.MakeGenericMethod(typeof(StructWithManagedField));
        Assert.NotNull(closed);
        Assert.Equal(typeof(StructWithManagedField), closed.GetGenericArguments()[0]);

        // ... so the wire asks the question itself.
        Assert.True(PrefabEditWire.ContainsManagedReferences(typeof(StructWithManagedField), out string reason));
        Assert.Contains("managed reference", reason);
        Assert.False(PrefabEditWire.ContainsManagedReferences(typeof(BlittableStruct), out string ok));
        Assert.Equal("", ok);
        Assert.True(PrefabEditWire.ContainsManagedReferences(null, out _));
    }

    // ---- one measure on the wire: the marshaled layout ----------------------------------------

    /// <summary>
    /// The sender and the receiver now use the same measure. The premise is measured here too:
    /// for a struct with <c>bool</c> fields — the shape this game's components have —
    /// <c>Unsafe.SizeOf</c> and <c>Marshal.SizeOf</c> disagree, so a reader that produced one and
    /// a receiver that validated the other refused the record (or, at equal sizes, parsed it at
    /// the wrong offsets).
    /// </summary>
    [Fact]
    public void The_wire_carries_the_marshalled_layout_at_both_ends()
    {
        int managed = System.Runtime.CompilerServices.Unsafe.SizeOf<StructWithBool>();
        int marshalled = System.Runtime.InteropServices.Marshal.SizeOf(typeof(StructWithBool));
        Assert.NotEqual(managed, marshalled);

        var value = new StructWithBool { Flag = true, Other = false, Number = 0x0BADF00D };
        Assert.True(PrefabEditWire.TryMarshalBytes(value, typeof(StructWithBool), out var bytes, out string error));
        Assert.Equal("", error);
        Assert.Equal(marshalled, bytes.Length);
        Assert.True(PrefabEditWire.SizeMatches(bytes.Length, marshalled));

        // The receiver's half: the same bytes rebuild the same value.
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var back = (StructWithBool)System.Runtime.InteropServices.Marshal.PtrToStructure(
                buffer, typeof(StructWithBool))!;
            Assert.True(back.Flag);
            Assert.Equal(0x0BADF00D, back.Number);
            Assert.False(back.Other);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>A value the marshaler cannot measure is refused with a reason, never guessed at.</summary>
    [Fact]
    public void A_value_the_marshaller_cannot_measure_is_refused()
    {
        Assert.False(PrefabEditWire.TryMarshalBytes(null, typeof(BlittableStruct), out var none, out string error));
        Assert.Empty(none);
        Assert.Equal("no value", error);

        Assert.False(PrefabEditWire.TryMarshalBytes(
            new List<int>(), typeof(List<>), out _, out string generic));
        Assert.Contains("Marshal.SizeOf", generic);
    }

    /// <summary>
    /// The pin on the sender: neither reader measures the managed layout any more. Both go
    /// through <see cref="PrefabEditWire.TryMarshalBytes"/>, which is the receiver's measure.
    /// </summary>
    [Fact]
    public void Neither_reader_puts_the_managed_layout_on_the_wire()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditHandle.cs");
        SourcePin.AssertNoStatementContaining(source, "MemoryMarshal");

        SourcePin.AssertUncommented(
            SourcePin.MethodBody(source, "private void RegisterTypedReader<T>()"),
            "PrefabEditWire.TryMarshalBytes(value, type, out var bytes, out string error)");
        SourcePin.AssertUncommented(
            SourcePin.MethodBody(source, "private void RegisterMarshalledReader<T>()"),
            "PrefabEditWire.TryMarshalBytes(value, type, out var bytes, out string error)");
    }
}
