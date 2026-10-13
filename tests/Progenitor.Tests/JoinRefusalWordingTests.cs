using System;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINWORD1: a join this client stopped (every failing row is its own <c>ServerMustHave</c>
/// requirement the server does not run) must not be worded as the server's refusal. Live
/// 2026-09-27: the server asked about 0 mods and the panel still said "'&lt;server&gt;' refused this
/// connection" and "The server's reason".
/// </summary>
public class JoinRefusalWordingTests
{
    private static ModDenyRow YouRequire(string id) =>
        new(id, "0.1.0", "0.1.0", ModDenyOrigin.YouRequire, null);

    private static ModDenyRow ServerRequires(string id) =>
        new(id, "1.0", null, ModDenyOrigin.ServerRequires, id + ": expected 1.0, got missing");

    private const string ClientKick =
        "Mod mismatch: progenitor.necroarmy: expected 0.1.0, got missing; progenitor.sample: expected 0.1.0, got missing";

    [Fact]
    public void A_join_this_client_stopped_says_so()
    {
        var report = new ModDenyReport(ClientKick, "Aqf27TEYT", "", "", "", false,
            new[] { YouRequire("progenitor.necroarmy"), YouRequire("progenitor.sample") });

        Assert.True(ModDenyText.RefusedByThisClient(report));
        Assert.Equal(
            "Your client stopped this join: 2 of your mods need Aqf27TEYT to run them too, and it does not.",
            ModDenyText.Headline(report));
        Assert.StartsWith("Your client's join check: ", ModDenyText.KickReasonLine(report), StringComparison.Ordinal);

        string text = ModDenyText.Compose(report);
        Assert.DoesNotContain("refused this connection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The server's reason", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ModDenyText.NothingWasDownloaded, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ModDenyText.RestartNote, text, StringComparison.Ordinal);
        Assert.Contains(ModDenyText.YourModsFix, text, StringComparison.Ordinal);
        Assert.Contains("progenitor.sample  you require 0.1.0  you have 0.1.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void One_mod_reads_in_the_singular_with_the_server_name()
    {
        var report = new ModDenyReport(ClientKick, "127.0.0.1:9976", "Night Garden", "", "", false,
            new[] { YouRequire("progenitor.necroarmy") });
        Assert.Equal(
            "Your client stopped this join: 1 of your mods needs 'Night Garden' to run it too, and it does not.",
            ModDenyText.Headline(report));
    }

    [Fact]
    public void A_server_refusal_keeps_the_server_wording()
    {
        var report = new ModDenyReport("Mod mismatch: a.mod: expected 1.0, got missing", "127.0.0.1:9976", "Test", "", "", false,
            new[] { ServerRequires("a.mod") });
        Assert.False(ModDenyText.RefusedByThisClient(report));
        Assert.Equal("'Test' refused this connection: 1 mod does not match.", ModDenyText.Headline(report));
        Assert.StartsWith("The server's reason: ", ModDenyText.KickReasonLine(report), StringComparison.Ordinal);
        string text = ModDenyText.Compose(report);
        Assert.Contains(ModDenyText.NothingWasDownloaded, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ModDenyText.YourModsFix, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mixed_refusal_is_the_server_s()
    {
        var report = new ModDenyReport("Mod mismatch", "127.0.0.1:9976", "Test", "", "", false,
            new[] { ServerRequires("a.mod"), YouRequire("b.mod") });
        Assert.False(ModDenyText.RefusedByThisClient(report));
        Assert.Equal("'Test' refused this connection: 2 mods do not match.", ModDenyText.Headline(report));
    }

    [Fact]
    public void No_rows_is_not_a_client_refusal()
    {
        var report = new ModDenyReport("x", "h", "S", "", "", false, Array.Empty<ModDenyRow>());
        Assert.False(ModDenyText.RefusedByThisClient(report));
    }

    [Fact]
    public void Compose_is_exactly_the_panel_lines()
    {
        var report = new ModDenyReport(ClientKick, "h", "S", "", "", false, new[] { YouRequire("x.mod") });
        var all = new System.Collections.Generic.List<string>(ModDenyText.Summary(report));
        all.AddRange(ModDenyText.Details(report));
        Assert.Equal(string.Join("\n", all) + "\n", ModDenyText.Compose(report));
    }
}
