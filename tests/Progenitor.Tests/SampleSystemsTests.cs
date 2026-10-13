using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job J18 (RD6). The Sample's virtual-system example.
///
/// The registrations themselves bind <c>ProjectM</c> and <c>Progenitor.Systems</c> and are behind
/// <c>!PROGENITOR_TESTS</c>, so the three anchors and the two <c>SystemHooks</c> calls are read out
/// of the source through <see cref="SourcePin"/> the way <see cref="SampleGroupTests"/> reads the
/// group attribute. The parsing, the counters and every reply line compile here and are pinned
/// directly.
/// </summary>
public class SampleSystemsTests
{
    [Fact]
    public void A_bare_command_and_status_both_mean_status()
    {
        Assert.True(SampleSystems.TryParse(null, out var a));
        Assert.Equal(SampleSystems.Kind.Status, a);
        Assert.True(SampleSystems.TryParse("   ", out var b));
        Assert.Equal(SampleSystems.Kind.Status, b);
        Assert.True(SampleSystems.TryParse("STATUS", out var c));
        Assert.Equal(SampleSystems.Kind.Status, c);
    }

    [Fact]
    public void Each_verb_parses_case_insensitively()
    {
        Assert.True(SampleSystems.TryParse("on", out var on));
        Assert.Equal(SampleSystems.Kind.On, on);
        Assert.True(SampleSystems.TryParse(" Off ", out var off));
        Assert.Equal(SampleSystems.Kind.Off, off);
        Assert.True(SampleSystems.TryParse("Sweep", out var sweep));
        Assert.Equal(SampleSystems.Kind.Sweep, sweep);
        Assert.True(SampleSystems.TryParse("ORDER", out var order));
        Assert.Equal(SampleSystems.Kind.Order, order);
    }

    /// <summary>
    /// A typo must not silently mean <c>status</c>: <c>.sys of</c> would then look like it had
    /// removed the systems and quietly report that they are still on.
    /// </summary>
    [Fact]
    public void An_unknown_word_is_refused_rather_than_read_as_status()
    {
        Assert.False(SampleSystems.TryParse("of", out _));
        Assert.False(SampleSystems.TryParse("remove", out _));
        Assert.False(SampleSystems.TryParse("on off", out _));
    }

    [Fact]
    public void The_usage_line_names_every_verb()
    {
        foreach (string verb in new[] { "status", "on", "off", "sweep", "order" })
            Assert.Contains(verb, SampleSystems.UsageReply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_on_reply_names_all_three_systems_with_their_anchors_and_orders()
    {
        string reply = SampleSystems.OnReply();
        Assert.Contains(SampleSystems.SimSystemName, reply, StringComparison.Ordinal);
        Assert.Contains(SampleSystems.AfterSystemName, reply, StringComparison.Ordinal);
        Assert.Contains(SampleSystems.BeforeSystemName, reply, StringComparison.Ordinal);
        Assert.Contains("VirtualSystemAnchor.AfterSimulation", reply, StringComparison.Ordinal);
        Assert.Contains("AddAfter<ServerBootstrapSystem>", reply, StringComparison.Ordinal);
        Assert.Contains("AddBefore<ServerBootstrapSystem>", reply, StringComparison.Ordinal);
        Assert.Contains("order 10", reply, StringComparison.Ordinal);
        Assert.Contains("300", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thing a reader cannot learn from the method name. <c>ctx.Systems.RemoveAll()</c>
    /// is scoped to the plugin, so it takes out <c>sample.users</c> and <c>sample.hooks</c> too;
    /// if that sentence ever leaves the reply, the example becomes a trap.
    /// </summary>
    [Fact]
    public void The_sweep_reply_reports_the_int_and_admits_its_blast_radius()
    {
        string reply = SampleSystems.SweepReply(5);
        Assert.Contains("returned 5", reply, StringComparison.Ordinal);
        Assert.Contains("sample.users", reply, StringComparison.Ordinal);
        Assert.Contains("sample.hooks", reply, StringComparison.Ordinal);
        Assert.Contains("owner-scoped", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_off_reply_reports_the_handle_count_and_says_what_it_left_alone()
    {
        string reply = SampleSystems.OffReply(3);
        Assert.Contains("Disposed 3 virtual system handle(s)", reply, StringComparison.Ordinal);
        Assert.Contains("untouched", reply, StringComparison.Ordinal);
        Assert.Contains("nothing to dispose", SampleSystems.OffWhenOffReply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_lines_carry_the_three_counters_and_the_Describe_dump()
    {
        var lines = SampleSystems.StatusLines(true, 7, 8, 9, "fallback=group; slots=3");
        Assert.Equal(2, lines.Count);
        Assert.Contains("Demo is on", lines[0], StringComparison.Ordinal);
        Assert.Contains("updates=7", lines[0], StringComparison.Ordinal);
        Assert.Contains("updates=8", lines[0], StringComparison.Ordinal);
        Assert.Contains("updates=9", lines[0], StringComparison.Ordinal);
        Assert.Contains("ctx.Systems.Describe(): fallback=group; slots=3", lines[1], StringComparison.Ordinal);

        var off = SampleSystems.StatusLines(false, 0, 0, 0, "");
        Assert.Contains("Demo is off", off[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Describe()</c> and <c>DescribeOrder()</c> are multi-line dumps and a chat reply is one
    /// line; a raw newline in either would also let a dump forge a <c>Sample sys:</c> log line.
    /// </summary>
    [Fact]
    public void A_multi_line_dump_cannot_become_several_lines()
    {
        string dump = "Before:\n  1. a\nAfter:\r\n  1. b";
        string order = SampleSystems.OrderReply(dump);
        Assert.DoesNotContain("\n", order, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", order, StringComparison.Ordinal);
        Assert.Contains(" | ", order, StringComparison.Ordinal);
        Assert.Contains("SystemHooks.DescribeOrder<ServerBootstrapSystem>()", order, StringComparison.Ordinal);

        var status = SampleSystems.StatusLines(true, 1, 1, 1, dump);
        foreach (var line in status)
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_drops_control_characters_and_trims()
    {
        Assert.Equal("ab", SampleSystems.Clean("  ab  "));
        Assert.Equal("a | b", SampleSystems.Clean("a\nb"));
        Assert.Equal("", SampleSystems.Clean(null));
        // A dump that ends in a newline must not leave a dangling separator on the reply.
        Assert.Equal("ab", SampleSystems.Clean("  ab \n"));
    }

    /// <summary>
    /// The world-exists line has to say <em>which</em> of the two deliveries happened, because
    /// that is the only observable difference between a cold boot and a hot reload here.
    /// </summary>
    [Fact]
    public void The_world_exists_line_distinguishes_the_immediate_delivery_from_the_deferred_one()
    {
        string hot = SampleSystems.WorldExistsLine("Server", immediate: true);
        Assert.Contains("hot reload", hot, StringComparison.Ordinal);
        string cold = SampleSystems.WorldExistsLine("Server", immediate: false);
        Assert.Contains("per-frame tick", cold, StringComparison.Ordinal);
        Assert.Contains("'Server'", cold, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detach_line_reports_the_hook_count_and_why_it_is_unload_only()
    {
        string line = SampleSystems.DetachLine(6);
        Assert.Contains("dropped 6 hook(s)", line, StringComparison.Ordinal);
        Assert.Contains("assembly-scoped", line, StringComparison.Ordinal);
        Assert.Contains("unload", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_heartbeat_fires_every_300_updates_and_never_on_zero()
    {
        Assert.False(SampleSystems.IsHeartbeatFrame(0));
        Assert.False(SampleSystems.IsHeartbeatFrame(299));
        Assert.True(SampleSystems.IsHeartbeatFrame(300));
        Assert.True(SampleSystems.IsHeartbeatFrame(600));
        Assert.Equal(300, SampleSystems.HeartbeatFrames);
    }

    [Fact]
    public void The_heartbeat_line_names_the_system_and_its_anchor()
    {
        string line = SampleSystems.HeartbeatLine(SampleSystems.AfterSystemName, SampleSystems.AfterAnchorLabel, 600);
        Assert.Equal("sample.sys.after: 600 update(s) at AddAfter<ServerBootstrapSystem>.", line);
    }

    [Fact]
    public void The_three_counters_are_independent_and_reset_together()
    {
        SampleSystems.Reset();
        SampleSystems.CountSim();
        SampleSystems.CountSim();
        SampleSystems.CountAfter();
        Assert.Equal(2L, SampleSystems.SimFrames);
        Assert.Equal(1L, SampleSystems.AfterFrames);
        Assert.Equal(0L, SampleSystems.BeforeFrames);

        SampleSystems.Reset();
        Assert.Equal(0L, SampleSystems.SimFrames);
        Assert.Equal(0L, SampleSystems.AfterFrames);
    }

    // ---- source pins over the registrations, which cannot compile here ------------------------

    /// <summary>
    /// The point of the file is the three <em>different</em> anchors. Collapsing them to three
    /// <c>Add(name, cb)</c> calls would still work, still log, and demonstrate nothing.
    /// </summary>
    [Fact]
    public void All_three_anchor_forms_are_actually_used()
    {
        string src = Source();
        SourcePin.AssertUncommented(src, "ctx.Systems.Add(SimSystemName, VirtualSystemAnchor.AfterSimulation, OnSim, SimOrder)");
        SourcePin.AssertUncommented(src, "ctx.Systems.AddAfter<ServerBootstrapSystem>(AfterSystemName, OnAfter, ExplicitOrder)");
        SourcePin.AssertUncommented(src, "ctx.Systems.AddBefore<ServerBootstrapSystem>(BeforeSystemName, OnBefore, ExplicitOrder)");
    }

    [Fact]
    public void RemoveAll_and_Describe_are_both_called_and_their_results_are_reported()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "int removed = ctx.Systems.RemoveAll();");
        SourcePin.AssertUncommented(src, "ctx.Systems.Describe()");
    }

    /// <summary>
    /// The two <c>SystemHooks</c> members this file exists to demonstrate. The world waiter must
    /// come from the <b>static</b> API (<c>ctx.Hooks.WhenWorldExists</c> would leave
    /// <c>SystemHooks.RemoveAll</c> nothing of its own to count), and it must still be tracked on
    /// the context so unload drops it whatever Detach does.
    /// </summary>
    [Fact]
    public void The_world_waiter_uses_the_static_api_and_is_tracked_and_swept()
    {
        string src = Source();
        SourcePin.AssertUncommented(src, "SystemHooks.WhenWorldExists(Worlds.ServerName");
        SourcePin.AssertStatementExactly(src, "ctx.Track(waiter);");
        SourcePin.AssertUncommented(src, "int removed = SystemHooks.RemoveAll(owner);");
        SourcePin.AssertUncommented(src, "SystemHooks.DescribeOrder<ServerBootstrapSystem>()");
    }

    /// <summary>
    /// <c>SystemHooks.RemoveAll</c> matches by the owner's assembly, so a call from a command
    /// would take out every hook the Sample has. It belongs in <c>Detach</c> and nowhere else.
    /// </summary>
    [Fact]
    public void The_assembly_wide_hook_sweep_is_only_reachable_from_Detach()
    {
        string src = Source();
        // The call, not the prose: every mention in a doc comment is written without the
        // semicolon, so this counts statements only.
        const string call = "SystemHooks.RemoveAll(owner);";
        int detach = src.IndexOf("internal static void Detach()", StringComparison.Ordinal);
        int sweep = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(detach > 0 && sweep > 0);
        Assert.Equal(sweep, src.LastIndexOf(call, StringComparison.Ordinal));
        Assert.True(sweep > detach, "SystemHooks.RemoveAll(owner) must sit inside Detach, not in the command path.");
    }

    /// <summary>
    /// The generic registration form is the one this file contributes; <c>Register(typeof(...))</c>
    /// would compile and quietly remove it.
    /// </summary>
    [Fact]
    public void The_command_class_is_registered_through_the_generic_overload()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "ctx.Commands.Register<SampleSystemCommands>();");
        Assert.DoesNotContain("Register(typeof(SampleSystemCommands))", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every verb changes or reports process-wide state that other players' gameplay rides on
    /// (<c>sweep</c> can stop another mod feature's heartbeat), so the command is gated by the
    /// attribute — never by a hand-rolled <c>IsAdmin</c> check.
    /// </summary>
    [Fact]
    public void The_command_is_admin_gated_by_the_attribute()
    {
        string src = Source();
        SourcePin.AssertUncommented(src, "adminOnly: true");
        Assert.DoesNotContain("if (ctx.IsAdmin", src, StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleSystems.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleSystems.cs not found from " + AppContext.BaseDirectory);
    }
}
