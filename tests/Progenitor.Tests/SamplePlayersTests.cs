using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job J14 (RD6). Game-free coverage of <c>.kick</c>: argument validation, the reason cap, the
/// text the kicked client is shown, and the reply for each of the three outcomes. The two calls
/// being exampled — <c>PlayerControl.TryGetConnection</c> and <c>PlayerControl.Disconnect</c> —
/// need a live <c>ServerBootstrapSystem</c> and are behind <c>!PROGENITOR_TESTS</c>, so the
/// self-kick guard and the disconnect reason are read out of the source.
/// </summary>
[Collection("PluginLogger")]
public class SamplePlayersTests : IDisposable
{
    public SamplePlayersTests() => SampleLog.Clear();

    public void Dispose() => SampleLog.Clear();

    [Fact]
    public void A_blank_name_is_the_usage_line()
    {
        Assert.False(SamplePlayers.TryParse("   ", "because", out string name, out string reason, out string? refusal));
        Assert.Equal("", name);
        Assert.Equal("", reason);
        Assert.Equal("Usage: .kick <characterName> [reason...]", refusal);
        Assert.Equal(SamplePlayers.UsageReply, refusal);
    }

    /// <summary>
    /// An admin typing <c>.kick Bob</c> means it. A missing reason is a default, not a refusal;
    /// the client still gets told something rather than an empty string.
    /// </summary>
    [Fact]
    public void A_missing_reason_defaults_rather_than_refusing()
    {
        Assert.True(SamplePlayers.TryParse("Bob", "", out string name, out string reason, out string? refusal));

        Assert.Equal("Bob", name);
        Assert.Equal("No reason given.", reason);
        Assert.Equal(SamplePlayers.DefaultReason, reason);
        Assert.Null(refusal);
    }

    [Fact]
    public void The_reason_is_capped_rather_than_handed_whole_to_the_network_layer()
    {
        Assert.True(SamplePlayers.TryParse("Bob", new string('x', SamplePlayers.MaxReasonChars + 50),
            out _, out string reason, out _));

        Assert.EndsWith("…", reason, StringComparison.Ordinal);
        Assert.Equal(SamplePlayers.MaxReasonChars + 1, reason.Length);
    }

    /// <summary>
    /// The reason is player input and is about to be echoed into the server log and handed to
    /// the network layer as a string; a newline in a log line is a forged log line.
    /// </summary>
    [Fact]
    public void The_reason_cannot_carry_a_newline_into_the_log_or_the_wire()
    {
        Assert.True(SamplePlayers.TryParse("Bob", "griefing\nSample players: forged", out _, out string reason, out _));

        Assert.DoesNotContain("\n", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", SamplePlayers.DisconnectMessage("Admin", reason), StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_is_cleaned_the_same_way()
    {
        Assert.True(SamplePlayers.TryParse("  Bob  ", "why", out string name, out _, out _));
        Assert.Equal("Bob", name);
    }

    [Fact]
    public void The_message_the_client_is_shown_names_the_admin_and_the_reason()
    {
        Assert.Equal("Kicked by Admin: griefing", SamplePlayers.DisconnectMessage("Admin", "griefing"));
        Assert.Equal("Kicked by an admin: griefing", SamplePlayers.DisconnectMessage("", "griefing"));
    }

    [Fact]
    public void A_successful_kick_names_the_user_the_reason_and_both_calls()
    {
        string reply = SamplePlayers.KickReply("Bob", "griefing", hadConnection: true, disconnected: true);

        Assert.Contains("Kicked Bob (griefing)", reply, StringComparison.Ordinal);
        Assert.Contains("PlayerControl.TryGetConnection found", reply, StringComparison.Ordinal);
        Assert.Contains("carried the reason text to their client", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two failure shapes are different problems and must not read the same. A missing row in
    /// the approved-user table is "there is no session"; a row plus a false from
    /// <c>Disconnect</c> is "the network layer refused".
    /// </summary>
    [Fact]
    public void The_two_failures_are_told_apart()
    {
        string noConnection = SamplePlayers.KickReply("Bob", "griefing", hadConnection: false, disconnected: false);
        Assert.Contains("found no row for them in the approved-user table", noConnection, StringComparison.Ordinal);
        Assert.Contains("ghost user", noConnection, StringComparison.Ordinal);

        string refused = SamplePlayers.KickReply("Bob", "griefing", hadConnection: true, disconnected: false);
        Assert.Contains("the network layer refused the disconnect", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("no row", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmatched_name_says_only_connected_users_are_searched()
    {
        string reply = SamplePlayers.NoSuchUserReply("Bob");

        Assert.Contains("No connected player called 'Bob'", reply, StringComparison.Ordinal);
        Assert.Contains("only connected users are searched", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_self_kick_refusal_says_the_check_is_on_the_entity_and_not_the_name()
    {
        Assert.Contains("Refusing to kick you", SamplePlayers.SelfKickReply, StringComparison.Ordinal);
        Assert.Contains("your own user entity, not on the name", SamplePlayers.SelfKickReply, StringComparison.Ordinal);
    }

    // ---- source pins for the halves that need a live server world ----

    /// <summary>
    /// Two names can collide; the entity cannot. The guard must compare
    /// <c>ICommandContext.UserEntity</c>, not the string the caller typed.
    /// </summary>
    [Fact]
    public void The_self_kick_guard_compares_user_entities()
    {
        SourcePin.AssertStatement(Source(), "if (userEntity == ctx.UserEntity)");
    }

    /// <summary>
    /// <c>TryGetConnection</c> is asked separately so the reply can tell the two failures apart;
    /// <c>Disconnect</c> would collapse them into one bool.
    /// </summary>
    [Fact]
    public void The_connection_is_looked_up_before_the_disconnect_so_the_reply_can_say_which_failed()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "bool hadConnection = TryGetConnection(ctx.World, userEntity);");
        SourcePin.AssertStatement(src, "return PlayerControl.TryGetConnection(bootstrap, userEntity, out _);");
    }

    /// <summary>
    /// The reason text reaching the client is the whole reason this module exists rather than the
    /// game's own kick event, so the message argument and the reason code are both pinned.
    /// </summary>
    [Fact]
    public void The_disconnect_carries_a_message_and_the_Kicked_reason()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "bool disconnected = PlayerControl.Disconnect(");
        SourcePin.AssertStatementExactly(src, "ctx.World, userEntity, message, ConnectionStatusChangeReason.Kicked);");
    }

    /// <summary>
    /// Offline players are excluded on purpose here — they have no session to close — which is
    /// the opposite call from <c>.ecs users</c>, and the one the lookup must keep making.
    /// </summary>
    [Fact]
    public void The_lookup_searches_connected_users_only()
    {
        SourcePin.AssertStatement(
            Source(),
            "var users = Query.Entities(em, includeDisabled: false, includeSystems: false,");
    }

    /// <summary>The gate is the attribute, never a check inside the body.</summary>
    [Fact]
    public void The_command_is_admin_gated_by_the_attribute()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "[Command(\"kick\", adminOnly: true,");
        Assert.DoesNotContain("if (!ctx.IsAdmin)", src, StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SamplePlayers.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SamplePlayers.cs not found from " + AppContext.BaseDirectory);
    }
}
