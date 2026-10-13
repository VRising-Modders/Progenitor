using System;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Source pins for the Sample routing example. The command classes themselves need the game
/// assembly; the owner ids, reply strings and Plugin.cs registration are what this file locks.
/// </summary>
public sealed class CommandSampleRoutingTests
{
    [Fact]
    public void Plugin_cs_registers_SampleRouting_on_the_server()
    {
        string plugin = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertStatementExactly(plugin, "SampleRouting.Attach(ctx);");
        int server = plugin.IndexOf("if (ctx.IsServer)", StringComparison.Ordinal);
        int attach = plugin.IndexOf("SampleRouting.Attach(ctx);", StringComparison.Ordinal);
        Assert.True(server > 0 && attach > server, "SampleRouting.Attach must stay in the server-only OnLoad block.");
    }

    [Fact]
    public void Example_uses_two_owner_ids_and_the_documented_replies()
    {
        string src = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleRouting.cs");
        SourcePin.AssertStatementExactly(src, "internal const string AlphaId = \"progenitor.sample.alpha\";");
        SourcePin.AssertStatementExactly(src, "internal const string BetaId = \"progenitor.sample.beta\";");
        SourcePin.AssertStatementExactly(src, "internal const string SparkReply = \"spark from alpha\";");
        SourcePin.AssertStatementExactly(src, "internal const string FlareAlphaReply = \"flare from alpha\";");
        SourcePin.AssertStatementExactly(src, "internal const string FlareBetaReply = \"flare from beta\";");
        SourcePin.AssertStatementExactly(src, "internal const string IronReply = \"route iron from alpha\";");
        SourcePin.AssertStatementExactly(src, "internal const string SteelReply = \"route steel from beta\";");
        SourcePin.AssertUncommented(src, "CommandRegistry.RegisterType(typeof(AlphaCommands), ctx.Owner, AlphaId)");
        SourcePin.AssertUncommented(src, "CommandRegistry.RegisterType(typeof(BetaCommands), ctx.Owner, BetaId)");
        SourcePin.AssertUncommented(src, "[CommandGroup(SampleRouting.RouteGroup)]");
        SourcePin.AssertUncommented(src, "[Command(SampleRouting.FlareName)]");
    }
}
