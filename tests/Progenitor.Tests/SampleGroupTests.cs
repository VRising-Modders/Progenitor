using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX5 (RD6 J12). The Sample's <c>[CommandGroup]</c> example.
///
/// The group class itself binds <c>ICommandContext</c> and is behind <c>!PROGENITOR_TESTS</c>, so
/// the attribute and the instance registration are read out of the source the way
/// <see cref="SampleCommandsTests"/> reads the <c>adminOnly</c> flags — the assertions are on the
/// arguments, not on prose. The reply text and the <c>RawText</c> sanitizing compile here and are
/// pinned directly.
/// </summary>
public class SampleGroupTests
{
    [Fact]
    public void The_group_attribute_carries_the_name_and_the_shorthand_the_helper_declares()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "[CommandGroup(SampleGroup.GroupName, SampleGroup.GroupShortHand)]");
        SourcePin.AssertStatement(src, "internal const string GroupName = \"sample\";");
        SourcePin.AssertStatement(src, "internal const string GroupShortHand = \"sam\";");
    }

    /// <summary>
    /// The point of the example is the <b>instance</b> overload. <c>Register(typeof(...))</c>
    /// would compile, register the same two commands, and quietly remove the only thing this file
    /// demonstrates, so the call is pinned as a whole statement.
    /// </summary>
    [Fact]
    public void The_group_is_registered_as_an_instance_and_not_as_a_type()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "ctx.Commands.Register(group);");
        Assert.DoesNotContain("Register(typeof(SampleGroupCommands))", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_echo_member_takes_a_Remainder_parameter()
    {
        SourcePin.AssertStatement(Source(), "public void Echo(ICommandContext ctx, [Remainder] string text = \"\")");
    }

    /// <summary>
    /// Neither member changes world state — <c>echo</c> answers the caller and <c>who</c> reports
    /// the caller's own flags — so neither is gated, and neither should quietly become gated
    /// either. The rule this pins is <see cref="SampleCommands"/>'s: the gate is the attribute.
    /// </summary>
    [Fact]
    public void Neither_group_member_is_admin_gated_because_neither_changes_world_state()
    {
        string src = Source();
        Assert.DoesNotContain("adminOnly: true", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Echo_refuses_an_empty_argument_with_the_usage_line()
    {
        Assert.Equal(SampleGroup.EchoUsageReply, SampleGroup.EchoEmptyReply());
    }

    [Fact]
    public void Echo_numbers_the_reply_from_the_instance_counter()
    {
        Assert.Contains("echo #1:", SampleGroup.EchoReply(1, "hello there"), StringComparison.Ordinal);
        Assert.Contains("echo #7:", SampleGroup.EchoReply(7, "hello there"), StringComparison.Ordinal);
        Assert.Contains("hello there", SampleGroup.EchoReply(1, "hello there"), StringComparison.Ordinal);
    }

    [Fact]
    public void Echo_is_cut_at_MaxEchoChars_rather_than_sending_the_whole_line_back()
    {
        string reply = SampleGroup.EchoReply(1, new string('x', SampleGroup.MaxEchoChars + 50));
        Assert.Contains("…", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', SampleGroup.MaxEchoChars + 1), reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>RawText</c> is the raw chat line. Echoing it into a log unsanitized is how a player
    /// forges a log line, so the example elides it through <see cref="SampleChat.Elide"/>.
    /// </summary>
    [Fact]
    public void RawText_cannot_forge_a_log_line()
    {
        var lines = SampleGroup.WhoLines(false, ".sample who\nSample group: forged", 1);
        foreach (var line in lines)
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Who_reports_both_context_members_and_the_instance_count()
    {
        var admin = SampleGroup.WhoLines(true, ".sample who", 3);
        Assert.Contains(admin, l => l.Contains("IsAdmin=True", StringComparison.Ordinal));
        Assert.Contains(admin, l => l.Contains("RawText=\".sample who\"", StringComparison.Ordinal));
        Assert.Contains(admin, l => l.Contains("served 3 command(s)", StringComparison.Ordinal));

        var player = SampleGroup.WhoLines(false, ".sample who", 1);
        Assert.Contains(player, l => l.Contains("IsAdmin=False", StringComparison.Ordinal));
    }

    /// <summary>
    /// A reader who copies this file must not copy a hand-rolled gate. The reply says in words
    /// that <c>IsAdmin</c> is read for reporting only.
    /// </summary>
    [Fact]
    public void The_IsAdmin_line_says_it_is_not_a_gate()
    {
        var lines = SampleGroup.WhoLines(true, ".sample who", 1);
        Assert.Contains(lines, l => l.Contains("never for a hand-rolled gate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_line_names_both_members_and_the_alias()
    {
        string line = SampleGroup.RegisteredLine();
        Assert.Contains(".sample echo", line, StringComparison.Ordinal);
        Assert.Contains(".sample who", line, StringComparison.Ordinal);
        Assert.Contains(".sam", line, StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleGroup.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleGroup.cs not found from " + AppContext.BaseDirectory);
    }

    // ---- Job J21: the explicit-owner CommandRegistry forms ----

    /// <summary>
    /// The only thing the explicit-owner forms change is who holds the handle, and the reply has
    /// to say that or a reader will think they differ in what gets registered.
    /// </summary>
    [Fact]
    public void The_registry_lines_name_both_forms_and_say_the_caller_holds_the_handle()
    {
        var lines = SampleGroup.RegistryLines(typeRegistered: true, instanceRegistered: true, uses: 2);

        Assert.Equal(4, lines.Count);
        Assert.Contains("CommandRegistry.RegisterType(typeof(SampleGroupOwnedCommands), ctx.Owner) -> registered", lines[0], StringComparison.Ordinal);
        Assert.Contains("CommandRegistry.RegisterInstance(", lines[0], StringComparison.Ordinal);
        Assert.Contains("ctx.Owner) -> registered", lines[0], StringComparison.Ordinal);
        Assert.Contains("only who holds the handle", lines[1], StringComparison.Ordinal);
        Assert.Contains("disposes both in", lines[1], StringComparison.Ordinal);
        Assert.Contains("This instance has served 2 command(s).", lines[3], StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap the explicit-owner forms invite: undoing one of them with
    /// <c>UnregisterOwner(ctx.Owner)</c>, which drops every command the plugin has. The reply
    /// names it, and the source never does it.
    /// </summary>
    [Fact]
    public void The_registry_lines_warn_against_UnregisterOwner_and_the_source_never_calls_it()
    {
        var lines = SampleGroup.RegistryLines(true, true, 1);

        Assert.Contains(lines, l => l.Contains("Never reach for CommandRegistry.UnregisterOwner(ctx.Owner)", StringComparison.Ordinal));
        // With the semicolon, so the pin is over a *call* and not over the sentence above, which
        // names the method on purpose.
        SourcePin.AssertNoStatementContaining(Source(), "UnregisterOwner(ctx.Owner);");
    }

    /// <summary>
    /// <c>RegisterAssembly</c> has no example here and the reason is a property of this plugin,
    /// not of the API: everything is already registered by name, so a scan would register it all
    /// twice under one owner. The sentence is a constant so the api.md row and the chat reply
    /// cannot drift apart.
    /// </summary>
    [Fact]
    public void The_reason_the_Sample_never_calls_RegisterAssembly_is_stated_and_is_about_this_plugin()
    {
        Assert.Contains("does not call RegisterAssembly", SampleGroup.WhyNoRegisterAssembly, StringComparison.Ordinal);
        Assert.Contains("register each of them twice under one owner", SampleGroup.WhyNoRegisterAssembly, StringComparison.Ordinal);
        Assert.Contains("registers nothing by hand", SampleGroup.WhyNoRegisterAssembly, StringComparison.Ordinal);
        Assert.Contains(SampleGroup.WhyNoRegisterAssembly, SampleGroup.RegistryLines(true, true, 1)[2], StringComparison.Ordinal);
        SourcePin.AssertNoStatementContaining(Source(), "RegisterAssembly(");
    }

    /// <summary>
    /// The two calls, with the owner passed explicitly. Pinned as whole statements: dropping the
    /// owner argument would silently turn them back into the <c>ctx.Commands</c> path this file
    /// already demonstrates elsewhere.
    /// </summary>
    [Fact]
    public void Both_explicit_owner_forms_are_called_with_ctx_Owner()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "_ownedTypeHandle = CommandRegistry.RegisterType(typeof(SampleGroupOwnedCommands), ctx.Owner);");
        SourcePin.AssertStatementExactly(src, "_ownedInstanceHandle = CommandRegistry.RegisterInstance(registryGroup, ctx.Owner);");
    }

    /// <summary>
    /// Nothing tracks these two handles, so <c>Detach</c> disposing them is the whole undo. A
    /// guard around either dispose would leave a command registered into a dead generation.
    /// </summary>
    [Fact]
    public void Detach_disposes_both_owned_handles()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "try { _ownedTypeHandle?.Dispose(); }");
        SourcePin.AssertStatementExactly(src, "try { _ownedInstanceHandle?.Dispose(); }");
        SourcePin.AssertStatementExactly(src, "_ownedTypeHandle = null;");
        SourcePin.AssertStatementExactly(src, "_ownedInstanceHandle = null;");
    }

    /// <summary>
    /// One class per form. Registering the same class through both would put two bundles under
    /// one name and demonstrate nothing, so the pin is that the two type names differ and each
    /// appears in exactly one registration call.
    /// </summary>
    [Fact]
    public void Each_form_registers_its_own_small_class()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "internal static class SampleGroupOwnedCommands");
        SourcePin.AssertStatement(src, "internal sealed class SampleGroupRegistryCommands");
        Assert.DoesNotContain("RegisterInstance(new SampleGroupOwnedCommands", src, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterType(typeof(SampleGroupRegistryCommands)", src, StringComparison.Ordinal);
    }
}
