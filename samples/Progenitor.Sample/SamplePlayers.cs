using System;

#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Players;
using Progenitor.Plugins;
using Stunlock.Network;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job J14 (RD6). The <c>Players</c> module's example: an admin-only
/// <c>.kick &lt;name&gt; [reason]</c> through <c>PlayerControl.TryGetConnection</c> and
/// <c>PlayerControl.Disconnect</c>. Both are documented <em>verified live</em> in
/// <c>docs/api.md</c> and had <b>none</b> in the Example column.
///
/// <b>Why the module exists at all.</b> The game's own kick event drops a player with no text;
/// <c>PlayerControl.Disconnect</c> goes through <c>ServerBootstrapSystem._NetServer.Disconnect</c>
/// with a <see cref="Stunlock.Network.ConnectionStatusChangeReason"/> and a free-text message. The
/// message lands in the client's disconnect-reason extra data and the server log, but the game's
/// disconnect screen does not show it: it prints only the reason code's text ("You were kicked
/// from the server" for Kicked; live 2026-10-05, see <c>PlayerControl</c>). To tell the player
/// why, whisper it in chat before the kick.
///
/// <b><c>TryGetConnection</c> is reported, not assumed.</b> <c>Disconnect</c> calls it internally
/// and returns false when there is no connection, which collapses "no such session" and "the
/// send failed" into one bool. The command asks first and prints the answer, because those are
/// two different things for whoever is reading the reply: a user entity with no row in the
/// approved-user table is not connected at all (a ghost, or a stale entity), while a row that is
/// there and a disconnect that still returns false is a network-layer problem.
///
/// <b>Refusing to kick yourself.</b> The check is on the caller's own <c>UserEntity</c>, not on
/// the name: two players can share a display name in principle, and the entity is what
/// <c>Disconnect</c> acts on. An admin who wants to leave can close the game.
///
/// <b>The reason is player input.</b> It is cleaned and capped at
/// <see cref="MaxReasonChars"/> before it goes anywhere — it is about to be handed to the
/// network layer as a string and echoed into the server log, and a newline in a log line is a
/// forged log line.
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SamplePlayers
{
    /// <summary>Log/grep area: <c>Sample players: …</c>.</summary>
    internal const string Area = "players";

    /// <summary>Longest kick reason this example will pass to the network layer, in characters.</summary>
    internal const int MaxReasonChars = 100;

    /// <summary>The reason used when the admin gave none.</summary>
    internal const string DefaultReason = "No reason given.";

    /// <summary>Usage line.</summary>
    internal const string UsageReply = "Usage: .kick <characterName> [reason...]";

    /// <summary>Reply when an admin aims the command at themselves.</summary>
    internal const string SelfKickReply =
        "Refusing to kick you. The check is on your own user entity, not on the name, because "
        + "that is what PlayerControl.Disconnect acts on.";

    /// <summary>Reply when no connected player matched the name.</summary>
    internal static string NoSuchUserReply(string name) =>
        "No connected player called '" + name + "'. Names are matched whole and case-insensitively, "
        + "and only connected users are searched - an offline player has nothing to disconnect.";

    /// <summary>
    /// Validate the two arguments. A blank name is the usage line; the reason is cleaned, capped,
    /// and defaulted rather than refused, because an admin typing <c>.kick Bob</c> means it.
    /// </summary>
    internal static bool TryParse(string? name, string? reason, out string cleanName, out string cleanReason, out string? refusal)
    {
        cleanName = SampleChat.Clean(name);
        cleanReason = SampleChat.Clean(reason);
        if (cleanName.Length == 0)
        {
            cleanReason = "";
            refusal = UsageReply;
            return false;
        }

        if (cleanReason.Length == 0)
            cleanReason = DefaultReason;
        else if (cleanReason.Length > MaxReasonChars)
            cleanReason = cleanReason.Substring(0, MaxReasonChars) + "…";

        refusal = null;
        return true;
    }

    /// <summary>
    /// The text the kicked client is shown. Named so the reply, the log and the wire all use the
    /// one string: a reader comparing the admin's chat reply against the other player's
    /// disconnect dialog should see the same words.
    /// </summary>
    internal static string DisconnectMessage(string by, string reason) =>
        "Kicked by " + (string.IsNullOrEmpty(by) ? "an admin" : by) + ": " + reason;

    /// <summary>
    /// The reply. It names the user and the reason, says whether
    /// <c>PlayerControl.TryGetConnection</c> found a row in the approved-user table, and says
    /// whether <c>Disconnect</c> returned true.
    /// </summary>
    internal static string KickReply(string name, string reason, bool hadConnection, bool disconnected)
    {
        if (disconnected)
            return "Kicked " + name + " (" + reason + "). PlayerControl.TryGetConnection found "
                + "their connection in the approved-user table, and Disconnect carried the reason "
                + "text to their client - the game's own kick event cannot.";

        return hadConnection
            ? "Did not kick " + name + " (" + reason + "). TryGetConnection found their connection, "
              + "but PlayerControl.Disconnect returned false: the network layer refused the "
              + "disconnect. See the server log."
            : "Did not kick " + name + " (" + reason + "). PlayerControl.TryGetConnection found no "
              + "row for them in the approved-user table, so there is no session to close - a "
              + "User entity can exist without one (a ghost user is exactly that).";
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Server-side registration. Static command class, so <c>Register(Type)</c>; the handle is
    /// tracked on the context and unload drops the command on every reason.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        ctx.Commands.Register(typeof(SamplePlayerCommands));
        SampleLog.Info(Area, "registered .kick (admin only; PlayerControl.TryGetConnection + Disconnect)");
    }

    /// <summary>Server entry point for <c>.kick</c>.</summary>
    internal static void HandleKick(ICommandContext ctx, string name, string reason)
    {
        if (!TryFindConnectedUser(ctx.World, name, out var userEntity))
        {
            ctx.Reply(NoSuchUserReply(name));
            SampleLog.Info(Area, "kick target=" + name + " no such connected user");
            return;
        }

        if (userEntity == ctx.UserEntity)
        {
            ctx.Reply(SelfKickReply);
            SampleLog.Info(Area, "kick refused: caller aimed at their own user entity");
            return;
        }

        bool hadConnection = TryGetConnection(ctx.World, userEntity);
        string message = DisconnectMessage(ctx.User.CharacterName.ToString(), reason);
        bool disconnected = PlayerControl.Disconnect(
            ctx.World, userEntity, message, ConnectionStatusChangeReason.Kicked);

        ctx.Reply(KickReply(name, reason, hadConnection, disconnected));
        SampleLog.Info(Area, "kick target=" + name + " connection=" + hadConnection
            + " disconnected=" + disconnected + " reason=\"" + reason + "\"");
    }

    /// <summary>
    /// Ask the approved-user table directly, so the reply can tell "no session" apart from
    /// "the disconnect failed". The <c>NetConnectionId</c> itself is deliberately not printed:
    /// it is a reference type over a native pointer and means nothing to a reader.
    /// </summary>
    private static bool TryGetConnection(World world, Entity userEntity)
    {
        try
        {
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return false;
            return PlayerControl.TryGetConnection(bootstrap, userEntity, out _);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .kick connection lookup threw: " + e);
            return false;
        }
    }

    /// <summary>
    /// Whole-name, case-insensitive lookup over connected users — the same shape
    /// <see cref="SampleChat"/>'s whisper uses. The filter that matters is
    /// <c>user.IsConnected</c> in the loop: an offline player's User entity is <em>not</em>
    /// disabled — a disconnect only removes <c>ConnectedUser</c>/<c>IsConnected</c> — and an
    /// offline player has no session to close, so a match on one could not be acted on.
    /// <c>.ecs users</c> is the command that prints the other side of that trade.
    /// </summary>
    private static bool TryFindConnectedUser(World world, string name, out Entity userEntity)
    {
        userEntity = Entity.Null;
        try
        {
            var em = world.EntityManager;
            var users = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                if (!user.IsConnected) continue;
                if (!string.Equals(user.CharacterName.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                userEntity = users[i];
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .kick user lookup threw: " + e);
        }

        return false;
    }
#endif

    /// <summary>
    /// Unload. The command registration is tracked on the context, so Core removes it on every
    /// reason; there is no other state. A kick already delivered is not something an unload
    /// could undo anyway.
    /// </summary>
    internal static void Detach()
    {
    }
}

#if !PROGENITOR_TESTS
/// <summary>
/// The <c>.kick</c> command (job J14). Separate from <see cref="SamplePlayers"/> so the helper
/// class stays compilable in the game-free test project.
/// </summary>
internal static class SamplePlayerCommands
{
    // adminOnly: this removes another player from the server. It is the most obviously gated
    // command in the Sample, and the gate is the attribute - never a check in the body.
    [Command("kick", adminOnly: true, usage: ".kick <characterName> [reason...]", description: "Admin only. Disconnect a connected player with a reason their client is shown.")]
    public static void Kick(ICommandContext ctx, string characterName = "", [Remainder] string reason = "")
    {
        try
        {
            if (!SamplePlayers.TryParse(characterName, reason, out string name, out string why, out string? refusal))
            {
                ctx.Reply(refusal ?? SamplePlayers.UsageReply);
                return;
            }

            SamplePlayers.HandleKick(ctx, name, why);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .kick threw: " + e);
            ctx.Reply("Sample kick failed. See the server log.");
        }
    }
}
#endif
