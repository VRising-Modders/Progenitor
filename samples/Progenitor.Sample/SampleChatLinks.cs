using System;
using System.Globalization;
using Progenitor.Chat;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Chat links for mods (<c>ctx.Chat.OnModLink</c>, <c>ctx.Chat.OnLinkClicked</c>, <c>ChatLinks.ModLink</c>).
///
/// <para><b>Server.</b> <c>.chat link [text]</c> replies to the caller with a System line carrying a mod link token,
/// <c>[mod:progenitor.sample:ping:&lt;n&gt;|text]</c>, built with <see cref="ChatLinks.ModLink"/>, so the text the caller
/// typed is escaped inside the token and cannot end it early or add another.</para>
///
/// <para><b>Client.</b> <c>ctx.Chat.OnModLink(LinkOwner, …)</c> makes that link clickable and receives its click first:
/// it checks the payload is one it would send (a server line is still the joined server's input), shows a notice with it and who wrote the line, and returns true so nothing else runs. A player who types
/// the same token gets plain text: the handler does not accept player links. <c>ctx.Chat.OnLinkClicked</c> logs every
/// other link click and returns false, so Progenitor's own action (the item dialog, the browser prompt) still runs.
/// Both are tracked and removed on unload.</para>
///
/// <para>The token and every line are game-free so the test project compiles this file.</para>
/// </summary>
internal static class SampleChatLinks
{
    internal const string Area = "chat";

    /// <summary>The link owner this sample handles on the client and writes on the server.</summary>
    internal const string LinkOwner = "progenitor.sample";

    /// <summary>The one link type the sample sends.</summary>
    internal const string LinkType = "ping";

    internal const string DefaultText = "Open the sample link";

    /// <summary>Longest text the command takes for the link, in characters.</summary>
    internal const int MaxText = 40;

    private static int _sent;

    /// <summary>The reply line for <c>.chat link</c>: a sentence with the link token in it.</summary>
    internal static string LinkLine(int number, string? text)
    {
        string shown = SampleChat.Clean(text);
        if (shown.Length == 0) shown = DefaultText;
        if (shown.Length > MaxText) shown = shown.Substring(0, MaxText);
        return "Click this link: " + ChatLinks.ModLink(LinkOwner, LinkType, N(number), shown);
    }

    /// <summary>A payload the sample sends: a positive number of at most 9 digits. Anything else is ignored.</summary>
    internal static bool IsPingPayload(string? payload) =>
        !string.IsNullOrEmpty(payload) && payload!.Length <= 9 && int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0;

    /// <summary>The notice the client shows for a click on the sample's link.</summary>
    internal static (string Title, string Body) ClickNotice(string payload, ChatLinkSource source, string sender) =>
        ("Sample link", "Payload " + payload + ", on a line from "
            + (source == ChatLinkSource.Server ? "the server" : "player " + (sender.Length > 0 ? sender : "unknown")) + ".");

    /// <summary>The log line for any other link click.</summary>
    internal static string ObservedClick(ChatLinkKind kind, string target, ChatLinkSource source) =>
        "link clicked kind=" + kind + " target=" + SampleChat.Elide(target, 60) + " source=" + source;

#if !PROGENITOR_TESTS
    /// <summary>Both sides: the server's command, the client's two handlers (tracked by the context).</summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (ctx.IsServer) ctx.Commands.Register(typeof(SampleChatLinkCommands));
        if (!ctx.IsClient) return;
        ctx.Chat.OnModLink(LinkOwner, click =>
        {
            // A server line is still the joined server's input: act only on a payload this sample would send.
            if (!IsPingPayload(click.Payload))
            {
                SampleLog.Info(Area, "ignored a sample link with an unexpected payload");
                return true;
            }

            var (title, body) = ClickNotice(click.Payload, click.Source, click.Sender);
            ctx.Dialogs.Notice(title, body);
            SampleLog.Info(Area, "sample link clicked payload=" + click.Payload + " source=" + click.Source);
            return true;
        });
        ctx.Chat.OnLinkClicked(click =>
        {
            SampleLog.Info(Area, ObservedClick(click.Kind, click.Target, click.Source));
            return false;
        });
        SampleLog.Info(Area, "client handles chat links of '" + LinkOwner + "' and logs other link clicks.");
    }

    /// <summary>Server entry point for <c>.chat link</c>.</summary>
    internal static void HandleLink(ICommandContext ctx, string text)
    {
        int n = ++_sent;
        ctx.Reply(LinkLine(n, text));
        SampleLog.Info(Area, "sent sample link #" + N(n));
    }
#endif

    internal static void Detach() => _sent = 0;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}

#if !PROGENITOR_TESTS
/// <summary><c>.chat link [text]</c>.</summary>
[CommandGroup("chat")]
internal static class SampleChatLinkCommands
{
    [Command("link", usage: ".chat link [text]", description: "Reply with a System line carrying a mod link; on a client with the Sample, clicking it shows its payload.")]
    public static void Link(ICommandContext ctx, [Remainder] string text = "")
    {
        try
        {
            SampleChatLinks.HandleLink(ctx, text);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .chat link threw: " + e);
            ctx.Reply("Sample chat link failed. See the server log.");
        }
    }
}
#endif
