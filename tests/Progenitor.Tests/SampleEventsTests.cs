using System;
using System.Collections.Generic;
using Progenitor.Events;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX5 (RD6 J5). The Sample's event-coverage example, driven through its game-free half.
///
/// The subscriptions themselves need <c>ProjectM</c> types and are behind
/// <c>!PROGENITOR_TESTS</c>, so what is pinned here is everything the handlers actually decide:
/// the marker match, the cancel decision (which is the <c>ICancellable</c> demonstration), the
/// counters, the ring's eviction, and the parse that decides which branch <c>.events</c> takes.
/// Those are the parts a regression would break silently — a canceler that cancels while
/// disarmed, or a bare <c>.events cancel</c> that guesses a direction, both read as working.
/// </summary>
[Collection("SampleEvents")]
public class SampleEventsTests : IDisposable
{
    public SampleEventsTests() => SampleEvents.Detach();

    public void Dispose() => SampleEvents.Detach();

    // The expectation travels as an int because SampleEvents.Kind is internal and an xUnit
    // theory method has to be public.
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("status", "", 0)]
    [InlineData("cancel", "on", 1)]
    [InlineData("CANCEL", "Off", 2)]
    [InlineData("castle", "on", 3)]
    [InlineData("castle", "off", 4)]
    [InlineData("clear", "", 5)]
    public void TryParse_accepts_the_six_forms(string first, string second, int expected)
    {
        Assert.True(SampleEvents.TryParse(first, second, out var kind));
        Assert.Equal((SampleEvents.Kind)expected, kind);
    }

    [Theory]
    [InlineData("cancel", "")]        // a bare toggle would make the reply the only record
    [InlineData("castle", "")]
    [InlineData("cancel", "yes")]
    [InlineData("status", "on")]
    [InlineData("clear", "all")]
    [InlineData("subscribe", "")]
    public void TryParse_refuses_anything_else(string first, string second)
    {
        Assert.False(SampleEvents.TryParse(first, second, out _));
    }

    [Theory]
    [InlineData("!!hello", true)]
    [InlineData("   !!hello", true)]
    [InlineData("!!", true)]
    [InlineData("!hello", false)]
    [InlineData("hello !!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matches_only_a_line_that_starts_with_the_marker(string? text, bool expected)
    {
        Assert.Equal(expected, SampleEvents.Matches(text));
    }

    [Fact]
    public void The_canceller_does_nothing_until_it_is_armed()
    {
        Assert.False(SampleEvents.CancelArmed);
        Assert.False(SampleEvents.RecordChatMatch("Rowan", "!!quiet", alreadyCancelled: false));
        Assert.Equal(1, SampleEvents.ChatMatchedCount);
        Assert.Equal(0, SampleEvents.ChatCancelledCount);

        SampleEvents.SetCancelArmed(true);
        Assert.True(SampleEvents.RecordChatMatch("Rowan", "!!quiet", alreadyCancelled: false));
        Assert.Equal(2, SampleEvents.ChatMatchedCount);
        Assert.Equal(1, SampleEvents.ChatCancelledCount);
    }

    /// <summary>
    /// The line has to say which of the two things happened, because "matched" and "canceled"
    /// are the difference between the demonstration running and the demonstration being off.
    /// </summary>
    [Fact]
    public void The_chat_line_distinguishes_matched_from_cancelled()
    {
        string armed = SampleEvents.ChatCancelLine("Rowan", cancelled: true, alreadyCancelled: false, text: "!!x");
        string idle = SampleEvents.ChatCancelLine("Rowan", cancelled: false, alreadyCancelled: false, text: "!!x");

        Assert.Contains("CANCELED", armed, StringComparison.Ordinal);
        Assert.DoesNotContain("CANCELED", idle, StringComparison.Ordinal);
        Assert.Contains(".events cancel on", idle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A newline in chat text would forge a second log line. The example routes every piece of
    /// player input through <see cref="SampleChat.Elide"/>, so this is the pin that it did.
    /// </summary>
    [Fact]
    public void Chat_text_in_the_log_line_cannot_forge_a_second_line()
    {
        string line = SampleEvents.ChatCancelLine("Rowan", true, false, "!!one\nSample events: forged");
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_event_bumps_only_its_own_counter()
    {
        SampleEvents.RecordDisconnect("Rowan", "left");
        SampleEvents.RecordWorldLoaded(2, 1, "LoadPersistenceSystemV2.CopyDataFromPrefabs");
        SampleEvents.RecordCastlePlaced(7, "Rowan", 3, initialScan: true);
        SampleEvents.RecordCastleDestroyed(7, "Rowan");

        Assert.Equal(1, SampleEvents.DisconnectCount);
        Assert.Equal(1, SampleEvents.WorldLoadCount);
        Assert.Equal(1, SampleEvents.CastlePlacedCount);
        Assert.Equal(1, SampleEvents.CastleDestroyedCount);
        Assert.Equal(0, SampleEvents.ChatMatchedCount);
    }

    [Fact]
    public void The_initial_scan_is_named_in_the_line_so_it_is_not_read_as_a_new_castle()
    {
        Assert.Contains("initial scan", SampleEvents.CastlePlacedLine(7, "Rowan", 3, initialScan: true), StringComparison.Ordinal);
        Assert.DoesNotContain("initial scan", SampleEvents.CastlePlacedLine(7, "Rowan", 3, initialScan: false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ring_keeps_the_last_Capacity_lines_oldest_first()
    {
        for (int i = 0; i < SampleEvents.Capacity + 3; i++)
            SampleEvents.RecordDisconnect("player" + i, "left");

        var recent = SampleEvents.RecentLines;
        Assert.Equal(SampleEvents.Capacity, recent.Count);
        Assert.Contains("player3", recent[0], StringComparison.Ordinal);
        Assert.Contains("player" + (SampleEvents.Capacity + 2), recent[recent.Count - 1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero subscribers is the state that means "the once-a-second castle poll is not running",
    /// and the status reply is the only place an operator can see it, so it says so in words.
    /// </summary>
    [Fact]
    public void Status_reports_the_castle_subscriber_counts_and_what_zero_means()
    {
        var lines = SampleEvents.StatusLines(0, 0);
        Assert.Contains(lines, l => l.Contains("castle subscribers placed=0 destroyed=0", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("poll is not running", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_with_nothing_recorded_names_the_triggers_rather_than_printing_an_empty_ring()
    {
        var lines = SampleEvents.StatusLines(1, 1);
        Assert.Contains(lines, l => l.Contains("Nothing yet.", StringComparison.Ordinal));
    }

    [Fact]
    public void Clear_forgets_the_counters_and_the_ring_but_not_the_armed_flag_after_Detach()
    {
        SampleEvents.SetCancelArmed(true);
        SampleEvents.RecordDisconnect("Rowan", "left");
        SampleEvents.ClearCounters();

        Assert.Equal(0, SampleEvents.DisconnectCount);
        Assert.Empty(SampleEvents.RecentLines);
        Assert.True(SampleEvents.CancelArmed);

        // Detach is the unload path and must leave nothing behind for the next generation.
        SampleEvents.Detach();
        Assert.False(SampleEvents.CancelArmed);
    }
}

/// <summary>
/// Job J19 (RD6). The <c>before</c>/<c>after</c> overload of <c>ctx.Events.On</c>, which had no
/// example anywhere, and the <c>.events order</c> that prints what it bought.
///
/// <para>The subscription itself needs a <c>PluginContext</c> and a <c>PlayerDisconnected</c>, so
/// it is behind <c>!PROGENITOR_TESTS</c>. What is asserted here is both halves of the claim: the
/// <b>lines</b> the Sample writes, and — through the real <see cref="EventBus"/> over a local event
/// type — that the ordering shape the Sample uses (a lower priority with an <c>after</c> naming a
/// higher one) actually resolves the way the reply says it does. Without the second half, the
/// reply would be a sentence about the bus rather than a report of it.</para>
/// </summary>
[Collection("SampleEvents")]
public class SampleEventsOrderTests
{
    private sealed class OrderProbe
    {
    }

    [Fact]
    public void The_order_token_parses_and_takes_no_argument()
    {
        Assert.True(SampleEvents.TryParse("order", "", out var kind));
        Assert.Equal(SampleEvents.Kind.Order, kind);

        Assert.True(SampleEvents.TryParse("ORDER", "", out kind));
        Assert.Equal(SampleEvents.Kind.Order, kind);

        Assert.False(SampleEvents.TryParse("order", "now", out _));
    }

    [Fact]
    public void The_usage_line_offers_it()
    {
        Assert.Contains(".events order", SampleEvents.UsageReply, StringComparison.Ordinal);
    }

    [Fact]
    // The whole point of the example: the audit handler asks for the higher priority and still
    // runs second. Driven through the real EventBus so this is a measurement of the bus and not a
    // restatement of the Sample's comment.
    public void The_after_constraint_beats_the_priority_number()
    {
        var ran = new List<string>();
        using var notice = EventBus.On<OrderProbe>(
            _ => ran.Add("notice"),
            priority: SampleEvents.NoticePriority,
            owner: this,
            pluginId: SampleEvents.NoticeId);
        using var audit = EventBus.On<OrderProbe>(
            _ => ran.Add("audit"),
            priority: SampleEvents.AuditPriority,
            owner: this,
            before: null,
            after: new[] { SampleEvents.NoticeId },
            pluginId: "progenitor.sample");

        EventBus.Publish(new OrderProbe());

        Assert.Equal(new[] { "notice", "audit" }, ran);
    }

    [Fact]
    // The control: without the after constraint the same two priorities run the other way round.
    // This is what makes the test above evidence rather than a coincidence of registration order.
    public void Without_the_constraint_the_higher_priority_runs_first()
    {
        var ran = new List<string>();
        using var notice = EventBus.On<OrderProbe>(
            _ => ran.Add("notice"), priority: SampleEvents.NoticePriority, owner: this, pluginId: SampleEvents.NoticeId);
        using var audit = EventBus.On<OrderProbe>(
            _ => ran.Add("audit"), priority: SampleEvents.AuditPriority, owner: this, pluginId: "progenitor.sample");

        EventBus.Publish(new OrderProbe());

        Assert.Equal(new[] { "audit", "notice" }, ran);
    }

    [Fact]
    // The two ids must differ. ctx.Events.On always passes the plugin's own guid, so a handler
    // that named that guid in `after` would be naming itself - which is why the notice handler
    // goes in through the static EventBus.On under an id of its own.
    public void The_notice_id_is_not_the_plugins_own_guid()
    {
        Assert.NotEqual("progenitor.sample", SampleEvents.NoticeId);
        Assert.True(SampleEvents.AuditPriority > SampleEvents.NoticePriority);
    }

    [Fact]
    public void The_order_reply_explains_the_constraint_and_then_prints_cores_dump_verbatim()
    {
        var lines = SampleEvents.OrderLines("EventBus PlayerDisconnected:\n  1. a prio=0\n  2. b prio=200 after=[a]");

        Assert.Equal(4, lines.Count);
        Assert.Contains("after=[" + SampleEvents.NoticeId + "]", lines[0], StringComparison.Ordinal);
        Assert.Equal("  EventBus PlayerDisconnected:", lines[1]);
        Assert.Equal("  1. a prio=0", lines[2]);
        Assert.Equal("  2. b prio=200 after=[a]", lines[3]);
    }

    [Fact]
    public void A_dump_with_no_handlers_says_so_instead_of_replying_a_bare_header()
    {
        foreach (string? dump in new[] { null, "", "   " })
        {
            var lines = SampleEvents.OrderLines(dump);
            Assert.Equal(2, lines.Count);
            Assert.Contains("no handlers are registered", lines[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Windows_line_endings_in_the_dump_do_not_leak_into_the_reply()
    {
        var lines = SampleEvents.OrderLines("EventBus X:\r\n  1. a prio=0\r\n");

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain("\r", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\r", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_audit_line_names_the_overload_and_why_it_ran_second()
    {
        string line = SampleEvents.AuditLine("Rowan", 3);

        Assert.Contains("audit #3", line, StringComparison.Ordinal);
        Assert.Contains("name=Rowan", line, StringComparison.Ordinal);
        Assert.Contains("ctx.Events.On(handler, priority=200, before: null, after: ["
            + SampleEvents.NoticeId + "])", line, StringComparison.Ordinal);
        Assert.Contains("runs AFTER the notice handler", line, StringComparison.Ordinal);
        Assert.Equal("Sample events: " + line, SampleLog.Format(SampleEvents.Area, line));
    }

    [Fact]
    public void An_unnamed_player_is_reported_rather_than_left_blank()
    {
        Assert.Contains("name=<unknown>", SampleEvents.AuditLine(null, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void The_audit_counter_counts_and_clears_with_the_others()
    {
        SampleEvents.Detach();
        Assert.Equal(0, SampleEvents.AuditCount);

        SampleEvents.RecordAudit("Rowan");
        SampleEvents.RecordAudit("Rowan");
        Assert.Equal(2, SampleEvents.AuditCount);
        Assert.Equal(2, SampleEvents.RecentLines.Count);

        SampleEvents.ClearCounters();
        Assert.Equal(0, SampleEvents.AuditCount);
        Assert.Empty(SampleEvents.RecentLines);
    }

    [Fact]
    // audits should track disconnects one for one; a status line that showed only one of the two
    // could not say that a subscription had been dropped.
    public void Status_reports_the_audit_count_beside_the_disconnect_count()
    {
        SampleEvents.Detach();
        SampleEvents.RecordDisconnect("Rowan", "left");
        SampleEvents.RecordAudit("Rowan");

        string first = SampleEvents.StatusLines(0, 0)[0];
        Assert.Contains("disconnects=1 audits=1", first, StringComparison.Ordinal);
        SampleEvents.Detach();
    }

    [Fact]
    // The Sample must register one of each route: the notice handler through the static
    // EventBus.On (so it can own NoticeId) and the audit handler through the context overload
    // (which is the member with no example). Collapsing either into the other loses the point.
    public void Attach_uses_the_static_route_for_the_notice_and_the_context_overload_for_the_audit()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleEvents.cs");

        SourcePin.AssertStatement(source, "EventBus.On<PlayerDisconnected>(");
        SourcePin.AssertStatement(source, "OnPlayerDisconnected, priority: NoticePriority, owner: ctx.Owner, pluginId: NoticeId),");
        SourcePin.AssertStatement(source, "ctx.Events.On<PlayerDisconnected>(");
        SourcePin.AssertStatement(source, "OnPlayerDisconnectedAudit, AuditPriority, before: null, after: new[] { NoticeId }),");

        // And `.events order` prints Core's live resolve, not a list the Sample kept itself.
        string handle = SourcePin.MethodBody(source, "internal static void HandleCommand(ICommandContext ctx, Kind kind)");
        SourcePin.AssertStatement(handle, "var order = OrderLines(EventBus.DescribeOrder<PlayerDisconnected>());");
    }
}

/// <summary>
/// Serializes the two suites that drive <see cref="SampleEvents"/>'s process-wide counters, ring
/// and armed flag. xUnit runs different classes in parallel, and both of these call
/// <c>SampleEvents.Detach()</c> and then assert a count.
/// </summary>
[CollectionDefinition("SampleEvents")]
public sealed class SampleEventsCollection
{
}
