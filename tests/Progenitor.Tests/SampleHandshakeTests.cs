using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job J19 (RD6). The Sample's handshake example.
///
/// <para>Two of these tests are the whole safety argument for the file existing, and they are the
/// reason it has tests at all rather than only a docstring:</para>
/// <list type="number">
/// <item><description><see cref="No_row_this_example_adds_can_refuse_a_join"/> — every row
/// <see cref="SampleHandshake.Plan"/> adds beyond the plugin's own is
/// <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> or
/// <see cref="NetworkCompatibilityLevel.ClientOnly"/>. A future edit that made one
/// <c>ServerMustHave</c> would kick the user's real client off the test server with a mod-mismatch
/// message and no obvious cause.</description></item>
/// <item><description><see cref="Plugin_cs_calls_Attach_before_DeclareSelf"/> — a source pin on
/// <c>Plugin.cs</c>. <c>RequireSelf</c>/<c>ProvideSelf</c> write an <em>undeclared</em> row for the
/// plugin's own guid, and they are only safe because <c>DeclareSelf()</c> overwrites it on the next
/// line. Reordering those two lines is a silent compatibility downgrade: nothing throws, nothing
/// logs, and the reverse-direction check just stops happening.</description></item>
/// </list>
///
/// <para>The <c>ctx.Handshake</c> calls themselves need a live <c>PluginContext</c> and are behind
/// <c>!PROGENITOR_TESTS</c>; the plan that drives them, the slot arithmetic and every reply line
/// are game-free and are what is asserted here.</para>
/// </summary>
public class SampleHandshakeTests : IDisposable
{
    private const string SelfGuid = "progenitor.sample";
    private const string SelfVersion = "1.0.0";

    public SampleHandshakeTests() => SampleHandshake.Reset();

    public void Dispose() => SampleHandshake.Reset();

    private static IReadOnlyList<SampleHandshake.Declaration> ServerPlan() =>
        SampleHandshake.Plan(
            server: true,
            SelfGuid,
            SelfVersion,
            NetworkCompatibilityLevel.ServerMustHave,
            NetworkVersionRule.Exact);

    private static IReadOnlyList<SampleHandshake.Declaration> ClientPlan() =>
        SampleHandshake.Plan(
            server: false,
            SelfGuid,
            SelfVersion,
            NetworkCompatibilityLevel.ServerMustHave,
            NetworkVersionRule.Exact);

    [Fact]
    public void The_server_plan_is_the_self_helper_then_the_two_explicit_rows_then_DeclareSelf()
    {
        var plan = ServerPlan();

        Assert.Equal(4, plan.Count);
        Assert.Equal(SampleHandshake.Route.RequireSelf, plan[0].Route);
        Assert.Equal(SampleHandshake.Route.RequireDeclared, plan[1].Route);
        Assert.Equal(SampleHandshake.Route.RequireDeclared, plan[2].Route);
        Assert.Equal(SampleHandshake.Route.DeclareSelf, plan[3].Route);

        Assert.Equal(SelfGuid, plan[0].Guid);
        Assert.Equal(SampleHandshake.ProtocolGuid, plan[1].Guid);
        Assert.Equal(SampleHandshake.UiGuid, plan[2].Guid);
        Assert.Equal(SelfGuid, plan[3].Guid);
    }

    [Fact]
    public void The_client_plan_is_the_same_rows_in_the_Provide_direction()
    {
        var server = ServerPlan();
        var client = ClientPlan();

        Assert.Equal(server.Count, client.Count);
        Assert.Equal(SampleHandshake.Route.ProvideSelf, client[0].Route);
        Assert.Equal(SampleHandshake.Route.ProvideDeclared, client[1].Route);
        Assert.Equal(SampleHandshake.Route.ProvideDeclared, client[2].Route);

        // Same guid, version, level and rule on both sides: that is what makes a declared row's
        // reverse-direction evaluation agree with itself instead of becoming a kick.
        for (int i = 0; i < server.Count; i++)
        {
            Assert.Equal(server[i].Guid, client[i].Guid);
            Assert.Equal(server[i].Version, client[i].Version);
            Assert.Equal(server[i].Level, client[i].Level);
            Assert.Equal(server[i].Rule, client[i].Rule);
        }
    }

    [Fact]
    // The load-bearing test. Every row this example adds beyond the plugin's own must be weaker
    // than the plugin's own ServerMustHave/Exact row, or the Sample starts refusing joins that
    // used to work - on the user's own client, with a mod-mismatch kick and no obvious cause.
    public void No_row_this_example_adds_can_refuse_a_join()
    {
        foreach (var plan in new[] { ServerPlan(), ClientPlan() })
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var row = plan[i];
                if (string.Equals(row.Guid, SelfGuid, StringComparison.Ordinal)) continue;

                Assert.True(
                    row.Level is NetworkCompatibilityLevel.NoNeedForSync or NetworkCompatibilityLevel.ClientOnly,
                    "row " + row.Guid + " is at " + row.Level
                    + "; a Sample row must never be ServerMustHave or it can kick a real client");
            }
        }
    }

    [Fact]
    // The self helpers are undeclared, which is *weaker* than what DeclareSelf writes. That is the
    // fact the ordering argument rests on, so it is asserted rather than assumed.
    public void The_self_helper_row_is_undeclared_and_DeclareSelf_row_is_declared()
    {
        var plan = ServerPlan();

        Assert.False(plan[0].Declared);
        Assert.True(plan[3].Declared);
        Assert.Equal(plan[0].Guid, plan[3].Guid);
    }

    [Fact]
    public void Without_the_attribute_DeclareSelf_is_recorded_as_the_undeclared_self_helper()
    {
        var plan = SampleHandshake.Plan(server: true, SelfGuid, SelfVersion, null, null);

        Assert.False(plan[3].Declared);
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, plan[3].Level);
        Assert.Equal(NetworkVersionRule.Exact, plan[3].Rule);
        Assert.Contains("no [NetworkCompatibility]", SampleHandshake.CallOf(plan[3].Route, plan[3].Declared), StringComparison.Ordinal);
    }

    [Fact]
    // Slots are the reason the example exists: Core's table is keyed by guid, so two calls on one
    // guid cost one slot, and a ClientOnly row costs none. Four rows, two slots.
    public void Slots_are_counted_per_guid_and_ClientOnly_is_free()
    {
        Assert.Equal(2, SampleHandshake.SlotsUsed(ServerPlan()));
        Assert.Equal(2, SampleHandshake.SlotsUsed(ClientPlan()));
        Assert.Equal(0, SampleHandshake.SlotsUsed(null));
        Assert.Equal(0, SampleHandshake.SlotsUsed(Array.Empty<SampleHandshake.Declaration>()));
    }

    [Fact]
    public void A_repeated_guid_takes_the_level_of_the_last_row_written()
    {
        // Core's table keeps the last write for a guid, so the slot arithmetic has to as well.
        // Free first, then paid: one slot.
        var freeThenPaid = new[]
        {
            new SampleHandshake.Declaration("a", "1", SampleHandshake.Route.RequireDeclared,
                NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None, declared: true),
            new SampleHandshake.Declaration("a", "1", SampleHandshake.Route.RequireDeclared,
                NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, declared: true),
        };
        Assert.Equal(1, SampleHandshake.SlotsUsed(freeThenPaid));

        // Paid first, then free: none. The last write is what survives.
        var paidThenFree = new[] { freeThenPaid[1], freeThenPaid[0] };
        Assert.Equal(0, SampleHandshake.SlotsUsed(paidThenFree));
    }

    [Fact]
    public void The_ClientOnly_row_is_the_only_free_one()
    {
        var plan = ServerPlan();

        Assert.True(plan[0].ConsumesSlot);
        Assert.True(plan[1].ConsumesSlot);
        Assert.False(plan[2].ConsumesSlot);
        Assert.True(plan[3].ConsumesSlot);
    }

    [Fact]
    public void Each_row_line_names_the_call_that_made_it()
    {
        var plan = ServerPlan();

        Assert.Contains("ctx.Handshake.RequireSelf()", SampleHandshake.RowLine(1, plan[0]), StringComparison.Ordinal);
        Assert.Contains("ctx.Handshake.Require(guid, version, level, rule)", SampleHandshake.RowLine(2, plan[1]), StringComparison.Ordinal);
        Assert.Contains("ctx.Handshake.DeclareSelf()", SampleHandshake.RowLine(4, plan[3]), StringComparison.Ordinal);

        Assert.Contains("ctx.Handshake.ProvideSelf()", SampleHandshake.RowLine(1, ClientPlan()[0]), StringComparison.Ordinal);
        Assert.Contains("ctx.Handshake.Provide(guid, version, level, rule)", SampleHandshake.RowLine(2, ClientPlan()[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_line_says_whether_the_row_is_declared_and_whether_it_costs_a_slot()
    {
        var plan = ServerPlan();

        string undeclared = SampleHandshake.RowLine(1, plan[0]);
        Assert.Contains("(undeclared: one direction, exact match)", undeclared, StringComparison.Ordinal);
        Assert.Contains("slot=1", undeclared, StringComparison.Ordinal);

        string free = SampleHandshake.RowLine(3, plan[2]);
        Assert.Contains("(declared: evaluated in both directions)", free, StringComparison.Ordinal);
        Assert.Contains("slot=0 (ClientOnly rows are free)", free, StringComparison.Ordinal);
    }

    [Fact]
    // The reply must not read as a view of the whole handshake table: Core keeps Required and
    // Provided private and this command can only see its own plugin's calls.
    public void The_slot_line_says_the_number_is_a_floor_and_names_MaxMods()
    {
        string line = SampleHandshake.SlotLine(2);

        Assert.Contains("Handshake.MaxMods=" + Handshake.MaxMods, line, StringComparison.Ordinal);
        Assert.Contains("this is a floor", line, StringComparison.Ordinal);
        Assert.Contains("ClientOnly", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reply_is_a_header_a_row_each_the_slot_line_and_the_ordering_note()
    {
        var plan = ServerPlan();
        var lines = SampleHandshake.Lines(plan, server: true);

        Assert.Equal(plan.Count + 3, lines.Count);
        Assert.Equal(SampleHandshake.HeaderLine(plan.Count, server: true), lines[0]);
        Assert.Equal(SampleHandshake.SlotLine(2), lines[^2]);
        Assert.Equal(SampleHandshake.OrderNote, lines[^1]);
    }

    [Fact]
    public void The_header_names_the_direction_because_Require_and_Provide_are_the_same_list_twice()
    {
        Assert.Contains("server side (Require direction)", SampleHandshake.HeaderLine(4, server: true), StringComparison.Ordinal);
        Assert.Contains("client side (Provide direction)", SampleHandshake.HeaderLine(4, server: false), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_ledger_replies_that_it_is_empty_rather_than_a_header_with_nothing_under_it()
    {
        string line = Assert.Single(SampleHandshake.Lines(Array.Empty<SampleHandshake.Declaration>(), server: true));
        Assert.Contains("declared no handshake rows", line, StringComparison.Ordinal);

        Assert.Single(SampleHandshake.Lines(null, server: false));
    }

    [Fact]
    public void The_ordering_note_is_printed_every_time_because_it_is_the_thing_a_copier_gets_wrong()
    {
        Assert.Contains("BEFORE Plugin.cs's DeclareSelf()", SampleHandshake.OrderNote, StringComparison.Ordinal);
        Assert.Contains("downgrade", SampleHandshake.OrderNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ledger_records_in_order_and_Reset_empties_it()
    {
        var plan = ServerPlan();
        for (int i = 0; i < plan.Count; i++)
            SampleHandshake.Note(plan[i]);

        var recorded = SampleHandshake.Declarations;
        Assert.Equal(plan.Count, recorded.Count);
        Assert.Equal(plan[0].Guid, recorded[0].Guid);
        Assert.Equal(plan[3].Route, recorded[3].Route);

        SampleHandshake.Reset();
        Assert.Empty(SampleHandshake.Declarations);
    }

    [Fact]
    public void Detach_empties_the_ledger_so_a_hot_reload_cannot_report_last_generations_rows()
    {
        SampleHandshake.Note(ServerPlan()[0]);
        Assert.NotEmpty(SampleHandshake.Declarations);

        SampleHandshake.Detach();
        Assert.Empty(SampleHandshake.Declarations);
    }

    [Fact]
    public void The_attach_line_says_the_rows_cannot_refuse_a_join()
    {
        string line = SampleHandshake.AttachedLine(4, 2, server: true);

        Assert.Contains("4 row(s)", line, StringComparison.Ordinal);
        Assert.Contains("2 of " + Handshake.MaxMods + " slot(s)", line, StringComparison.Ordinal);
        Assert.Contains("no new join can be refused", line, StringComparison.Ordinal);
        Assert.Equal("Sample handshake: " + line, SampleLog.Format(SampleHandshake.Area, line));
    }

    [Fact]
    // The pin that keeps the safety argument true. See the class docstring: swapping these two
    // lines downgrades the Sample's compatibility and nothing else would notice.
    public void Plugin_cs_calls_Attach_before_DeclareSelf()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");

        SourcePin.AssertStatementExactly(source, "SampleHandshake.Attach(ctx);");
        SourcePin.AssertStatementExactly(source, "ctx.Handshake.DeclareSelf();");

        int attach = source.IndexOf("SampleHandshake.Attach(ctx);", StringComparison.Ordinal);
        int declare = source.IndexOf("ctx.Handshake.DeclareSelf();", StringComparison.Ordinal);
        Assert.True(attach > 0 && declare > 0);
        Assert.True(
            attach < declare,
            "SampleHandshake.Attach(ctx) must run before ctx.Handshake.DeclareSelf(): RequireSelf/"
            + "ProvideSelf write an undeclared row that DeclareSelf overwrites. Reversed, this "
            + "example downgrades the Sample to a one-direction exact match.");
    }

    [Fact]
    // The guids the two extra rows use must not collide with the plugin's own, or the "two calls
    // on one guid cost one slot" arithmetic would quietly become the whole story and the
    // ClientOnly demonstration would vanish into an overwrite.
    public void The_extra_rows_use_guids_of_their_own()
    {
        Assert.NotEqual(SampleHandshake.ProtocolGuid, SampleHandshake.UiGuid);
        Assert.NotEqual(SelfGuid, SampleHandshake.ProtocolGuid);
        Assert.NotEqual(SelfGuid, SampleHandshake.UiGuid);
        Assert.True(SampleHandshake.ProtocolGuid.Length <= Handshake.MaxGuidChars);
        Assert.True(SampleHandshake.UiGuid.Length <= Handshake.MaxGuidChars);
        Assert.True(SampleHandshake.ProtocolVersion.Length <= Handshake.MaxVersionChars);
        Assert.True(SampleHandshake.UiVersion.Length <= Handshake.MaxVersionChars);
    }

    [Fact]
    // A guid or version Core refuses is a row that silently is not there, which would make `.hs`
    // report declarations the handshake never got. Handshake.Compare.TryValidateEntry is Core's
    // own gate, so the Sample's constants are run through it rather than eyeballed.
    public void Core_accepts_both_extra_rows()
    {
        Assert.True(Handshake.Compare.TryValidateEntry(
            SampleHandshake.ProtocolGuid, SampleHandshake.ProtocolVersion, out string? protocolError), protocolError);
        Assert.True(Handshake.Compare.TryValidateEntry(
            SampleHandshake.UiGuid, SampleHandshake.UiVersion, out string? uiError), uiError);
    }
}
