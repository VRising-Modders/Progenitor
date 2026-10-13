using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job J18 (RD6). The Sample's stateful command <b>instance</b>.
///
/// The class itself binds <c>ICommandContext</c> and is behind <c>!PROGENITOR_TESTS</c>, so the
/// registration form and the attribute are read out of the source through <see cref="SourcePin"/>.
/// The parsing and the reply text compile here and are pinned directly.
/// </summary>
public class SampleInstanceCommandsTests
{
    [Fact]
    public void A_bare_command_counts_and_reset_resets()
    {
        Assert.True(SampleInstanceCommands.TryParse(null, out var bare));
        Assert.Equal(SampleInstanceCommands.Kind.Count, bare);
        Assert.True(SampleInstanceCommands.TryParse("  ", out var blank));
        Assert.Equal(SampleInstanceCommands.Kind.Count, blank);
        Assert.True(SampleInstanceCommands.TryParse(" RESET ", out var reset));
        Assert.Equal(SampleInstanceCommands.Kind.Reset, reset);
    }

    /// <summary>
    /// <c>.counter clear</c> must not quietly count instead of resetting: a refused word replies
    /// usage, the same rule <see cref="SampleHooks"/> and <see cref="SampleSystems"/> follow.
    /// </summary>
    [Fact]
    public void An_unknown_word_is_refused()
    {
        Assert.False(SampleInstanceCommands.TryParse("clear", out _));
        Assert.False(SampleInstanceCommands.TryParse("reset now", out _));
        Assert.Contains(".counter reset", SampleInstanceCommands.UsageReply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reply is the example. If it stops saying that the number is instance state and not a
    /// static, the command is just a counter and the api.md row it fills means nothing.
    /// </summary>
    [Fact]
    public void The_count_reply_says_the_number_is_instance_state()
    {
        string reply = SampleInstanceCommands.CountReply(4);
        Assert.Contains("used 4 time(s)", reply, StringComparison.Ordinal);
        Assert.Contains("ctx.Commands.Register(object)", reply, StringComparison.Ordinal);
        Assert.Contains("not a static", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reset_reply_reports_the_previous_value_and_the_zero_case_is_separate()
    {
        Assert.Contains("it was 9", SampleInstanceCommands.ResetReply(9), StringComparison.Ordinal);
        Assert.Contains("already 0", SampleInstanceCommands.ResetWhenZeroReply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registration_and_detach_lines_name_the_form_and_the_handle()
    {
        Assert.Contains("ctx.Commands.Register(object)", SampleInstanceCommands.RegisteredLine(), StringComparison.Ordinal);
        Assert.Contains(".counter", SampleInstanceCommands.RegisteredLine(), StringComparison.Ordinal);
        Assert.Contains("disposed the .counter registration handle after 3 use(s)",
            SampleInstanceCommands.DetachLine(3), StringComparison.Ordinal);
    }

    /// <summary>
    /// No reply line may carry a newline: they all reach chat one line at a time and the log ring
    /// beside it.
    /// </summary>
    [Fact]
    public void No_reply_line_spans_two_lines()
    {
        foreach (string line in new[]
                 {
                     SampleInstanceCommands.UsageReply,
                     SampleInstanceCommands.RegisteredLine(),
                     SampleInstanceCommands.CountReply(1),
                     SampleInstanceCommands.ResetReply(1),
                     SampleInstanceCommands.ResetWhenZeroReply,
                     SampleInstanceCommands.DetachLine(1),
                 })
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    // ---- source pins over the registration, which cannot compile here --------------------------

    /// <summary>
    /// The whole point of the file. <c>Register&lt;SampleCounter&gt;()</c> or
    /// <c>Register(typeof(SampleCounter))</c> would compile, register the same command, and delete
    /// the only thing being demonstrated — Core would construct its own object and the plugin
    /// could never reach it.
    /// </summary>
    [Fact]
    public void The_counter_is_registered_as_an_instance_and_not_as_a_type()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "_handle = ctx.Commands.Register(counter);");
        Assert.DoesNotContain("Register(typeof(SampleCounter))", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Register<SampleCounter>()", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counter must be a field on the instance. A <c>static int</c> here would still make the
    /// command work and would silently turn the example into the thing it argues against — and
    /// the count would survive a reload, which is the observable the live check looks for.
    /// </summary>
    [Fact]
    public void The_count_is_an_instance_field_and_not_a_static()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "private int _count;");
        SourcePin.AssertNoStatementContaining(src, "private static int _count");
    }

    /// <summary>
    /// The returned <c>IDisposable</c> is the mechanism a plugin needs to retire a command while
    /// staying loaded, so the handle is kept and disposed rather than left to the context sweep.
    /// </summary>
    [Fact]
    public void The_returned_handle_is_kept_and_disposed()
    {
        string src = Source();
        SourcePin.AssertUncommented(src, "private static IDisposable? _handle;");
        SourcePin.AssertStatementExactly(src, "handle.Dispose();");
    }

    /// <summary>
    /// Neither form changes world state — the command reports a number it owns — so neither is
    /// gated, and neither should quietly become gated either.
    /// </summary>
    [Fact]
    public void The_counter_is_not_admin_gated_because_it_changes_nothing_in_the_world()
    {
        Assert.DoesNotContain("adminOnly", Source(), StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleInstanceCommands.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleInstanceCommands.cs not found from " + AppContext.BaseDirectory);
    }
}
