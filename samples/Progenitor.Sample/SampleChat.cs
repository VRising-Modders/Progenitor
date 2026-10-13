using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Plugins;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX1 (RD6 J2). The first <c>ctx.Chat</c> consumer in the repository, and the first
/// compiled subscriber to the cancellable <see cref="Progenitor.Events.ChatMessage"/> event.
/// Before this, both were verified only through the Dev bridge's <c>broadcast</c> op (evidence
/// 48, 55, 58, 60) — that is, by the framework talking to itself, never by a plugin.
///
/// Three things, all server-side:
/// <list type="bullet">
/// <item><description><c>.shout &lt;text&gt;</c> → <c>ctx.Chat.Broadcast</c>, one
/// <c>ChatMessageServerEvent</c> per connected user <em>including the speaker</em>, and the reply
/// carries the recipient count so the server log alone proves how many it reached.</description></item>
/// <item><description><c>.whisper &lt;name&gt; &lt;text&gt;</c> → <c>ctx.Chat.SendTo</c> on one
/// resolved user entity.</description></item>
/// <item><description>a <c>ChatMessage</c> subscriber at priority <see cref="ObserverPriority"/>
/// that logs one line per chat line with the sender and whether a command already canceled
/// it.</description></item>
/// </list>
///
/// <b>Why the observer's priority is negative.</b> The commands module subscribes at 500 and
/// cancels what it handles (<c>CommandSystem.cs:53</c>), and <c>EventBus.Publish</c> runs
/// <em>every</em> handler in descending priority regardless of cancellation. Subscribing below
/// commands is therefore the only way to observe the verdict: at a higher priority this handler
/// would log <c>handled=False</c> for every command line. A mod that wants to <em>make</em> the
/// decision goes above 500 instead; a mod that wants to <em>watch</em> it goes below.
///
/// <b>Why both send commands are admin-gated.</b> They put text of the caller's choosing into
/// other players' chat as a <c>System</c> message — the one message type players read as coming
/// from the server. That is an impersonation surface, not a world write, and the Sample's rule
/// (<see cref="SampleCommands"/>) is that ungating is the decision nobody revisits.
///
/// <b>Chat text is player input.</b> Nothing here interprets it: it is truncated to
/// <see cref="LogTextChars"/> for the log, control characters are folded, and the send path lets
/// Core truncate at the game's 509 UTF-8 byte limit (<c>ChatLogic.MaxUtf8Bytes</c>).
///
/// Parsing, sanitizing and every line are game-free so the test project compiles this file.
/// </summary>
internal static class SampleChat
{
    /// <summary>Log/grep area: <c>Sample chat: …</c>.</summary>
    internal const string Area = "chat";

    /// <summary>
    /// Priority of the observing subscriber. Below the commands module's 500 so a handled line
    /// is already canceled by the time this runs.
    /// </summary>
    internal const int ObserverPriority = -100;

    /// <summary>Longest chat text this plugin will accept from a command, in characters.</summary>
    internal const int MaxSendChars = 200;

    /// <summary>How much of an observed chat line reaches the log. The rest is elided.</summary>
    internal const int LogTextChars = 60;

    /// <summary>Usage line for <c>.shout</c>.</summary>
    internal const string ShoutUsageReply = "Usage: .shout <text>";

    /// <summary>Usage line for <c>.whisper</c>.</summary>
    internal const string WhisperUsageReply = "Usage: .whisper <name> <text>";

    /// <summary>
    /// Clean one line of chat-sourced text for a log or a send: control characters (a newline
    /// would forge a second log line) become spaces, runs of whitespace collapse, and the result
    /// is trimmed. Returns an empty string for null.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var buffer = new char[text!.Length];
        int n = 0;
        bool lastWasSpace = true; // leading whitespace is dropped
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) || c == ' ')
            {
                if (lastWasSpace) continue;
                buffer[n++] = ' ';
                lastWasSpace = true;
                continue;
            }

            buffer[n++] = c;
            lastWasSpace = false;
        }

        while (n > 0 && buffer[n - 1] == ' ') n--;
        return new string(buffer, 0, n);
    }

    /// <summary>Cut <paramref name="text"/> to <paramref name="max"/> characters, marking the cut.</summary>
    internal static string Elide(string? text, int max)
    {
        string clean = Clean(text);
        if (max < 1) return "";
        if (clean.Length <= max) return clean;
        return clean.Substring(0, max) + "…";
    }

    /// <summary>
    /// Whether a command's text argument is sendable. Empty is refused (an empty broadcast
    /// creates entities nobody can see); over <see cref="MaxSendChars"/> is refused rather than
    /// silently truncated, so the caller learns their line did not go out whole.
    /// </summary>
    internal static bool TryPrepareSend(string? text, out string prepared, out string? refusal)
    {
        prepared = Clean(text);
        if (prepared.Length == 0)
        {
            refusal = "Nothing to send: the text was empty.";
            return false;
        }

        if (prepared.Length > MaxSendChars)
        {
            refusal = "That line is " + N(prepared.Length) + " characters; the sample caps a send at "
                + N(MaxSendChars) + ". Shorten it.";
            prepared = "";
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>Reply for <c>.shout</c>, carrying the recipient count Core returned.</summary>
    internal static string ShoutReply(int recipients) =>
        recipients > 0
            ? "Broadcast to " + N(recipients) + " connected user(s), you included."
            : "Broadcast reached nobody: ctx.Chat.Broadcast returned 0 (no connected users, or the world is not ready).";

    /// <summary>Reply for <c>.whisper</c> when the target resolved.</summary>
    internal static string WhisperReply(string name, bool sent) =>
        sent
            ? "Sent to " + name + "."
            : "Could not send to " + name + ": ctx.Chat.SendTo refused (no ConnectedUser slot).";

    /// <summary>Reply for <c>.whisper</c> when no connected user matched.</summary>
    internal static string NoSuchUserReply(string name) =>
        "No connected player called '" + name + "'. Names are matched whole and case-insensitively.";

    /// <summary>The observer's line. One per chat line this server sees.</summary>
    internal static string ObservedLine(int type, string? sender, bool handled, string? text) =>
        "saw type=" + TypeName(type) + " from=" + (string.IsNullOrEmpty(sender) ? "<unknown>" : sender)
        + " handled=" + (handled ? "True" : "False") + " text=\"" + Elide(text, LogTextChars) + "\"";

    /// <summary>
    /// <c>ProjectM.Network.ChatMessageType</c> as a word. The mapping is the one
    /// <c>ChatLogic.ToServerChatMessageType</c> documents (0 Global … 5 System); an unknown value
    /// prints as its number rather than being guessed at.
    /// </summary>
    internal static string TypeName(int type) => type switch
    {
        0 => "Global",
        1 => "Region",
        2 => "Team",
        3 => "Whisper",
        4 => "Local",
        5 => "System",
        _ => N(type),
    };

#if !PROGENITOR_TESTS
    /// <summary>
    /// Server-side registration: one subscriber, tracked on the context so unload drops it.
    /// The send half needs no registration — <c>ctx.Chat</c> is stateless.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        ctx.Events.On<ChatMessage>(OnChat, priority: ObserverPriority);
        SampleLog.Info(Area, "observing ChatMessage at priority " + N(ObserverPriority)
            + " (below the commands module's 500, so handled= is the real verdict).");
    }

    private static void OnChat(ChatMessage ev)
    {
        try
        {
            SampleLog.Info(Area, ObservedLine((int)ev.Type, ev.User.CharacterName.ToString(), ev.IsCancelled, ev.Text));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample chat observer threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.shout</c>.</summary>
    internal static void HandleShout(ICommandContext ctx, string text)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        if (!TryPrepareSend(text, out string prepared, out string? refusal))
        {
            ctx.Reply(refusal ?? ShoutUsageReply);
            SampleLog.Info(Area, "shout refused: " + refusal);
            return;
        }

        int recipients = plugin.Chat.Broadcast(prepared, ChatMessageType.System);
        ctx.Reply(ShoutReply(recipients));
        SampleLog.Info(Area, "shout recipients=" + N(recipients) + " text=\"" + Elide(prepared, LogTextChars) + "\"");
    }

    /// <summary>Server entry point for <c>.whisper</c>.</summary>
    internal static void HandleWhisper(ICommandContext ctx, string name, string text)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        string target = Clean(name);
        if (target.Length == 0)
        {
            ctx.Reply(WhisperUsageReply);
            return;
        }

        if (!TryPrepareSend(text, out string prepared, out string? refusal))
        {
            ctx.Reply(refusal ?? WhisperUsageReply);
            SampleLog.Info(Area, "whisper refused: " + refusal);
            return;
        }

        if (!TryFindConnectedUser(ctx.World, target, out var userEntity))
        {
            ctx.Reply(NoSuchUserReply(target));
            SampleLog.Info(Area, "whisper to=" + target + " no such connected user");
            return;
        }

        bool sent = plugin.Chat.SendTo(userEntity, prepared, ChatMessageType.Whisper);
        ctx.Reply(WhisperReply(target, sent));
        SampleLog.Info(Area, "whisper to=" + target + " sent=" + sent
            + " text=\"" + Elide(prepared, LogTextChars) + "\"");
    }

    /// <summary>
    /// Whole-name, case-insensitive lookup over connected users. An offline player's User entity
    /// is <em>not</em> disabled — a disconnect only removes <c>ConnectedUser</c>/<c>IsConnected</c>
    /// — so the <c>user.IsConnected</c> test in the loop is what excludes them, not the query
    /// option; an offline player has no <c>ConnectedUser</c> slot to send to. Disabled entities
    /// are excluded here only because a disabled entity could never be a whisper target either.
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
            Plugin.Logger?.LogError("Sample chat user lookup threw: " + e);
        }

        return false;
    }
#endif

    /// <summary>
    /// Unload. The subscriber is tracked on the context, so Core removes it on every reason;
    /// there is no other state to drop. Kept for symmetry with the other Sample modules, and so
    /// a reader copying this file has the hook to put their own teardown in.
    /// </summary>
    internal static void Detach()
    {
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
