using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// CHAT4 (docs/chat.md, "Server settings (as built)"): where the Server tab's Chat settings are stored. Game-free.
/// <list type="bullet">
/// <item><b>Synced to players</b> (ConfigSync rows of <see cref="ConfigSync.FrameworkSettingsId"/>, admin-edited through
/// <see cref="ServerSettings"/>): <c>BepInEx/config/progenitor.serverchat.cfg</c>, section <c>[ServerChat]</c>. Every player
/// with Progenitor receives them; the client fills <c>ChatPolicy</c> from them.</item>
/// <item><b>Server only</b> (the admin channel, <c>'PrJa'</c>; <see cref="ServerOptionOps"/>): the server's
/// <c>progenitor.cfg</c>, sections <c>[ChatFilter]</c> and <c>[ChatRelay]</c>. Only admins receive them; the webhook address
/// is never sent (it reads <c>set</c> or empty).</item>
/// </list>
/// </summary>
internal static class ServerChatOptions
{
    // ---- synced to players ----------------------------------------------------------------------------------

    public const string FileName = "progenitor.serverchat.cfg";

    /// <summary>Not <c>Chat</c>: that section of <c>progenitor.ui.cfg</c> holds each player's own chat settings.</summary>
    public const string Section = "ServerChat";

    public const string AllowLinksKey = "AllowLinks";
    public const string AllowModLinksKey = "AllowModLinks";
    public const string AllowAtAllKey = "AllowAtAll";
    public const string AllowLongMessagesKey = "AllowLongMessages";
    public const string MaxLongMessageCharsKey = "MaxLongMessageChars";

    /// <summary>The synced keys, in page order.</summary>
    internal static readonly IReadOnlyList<string> SyncedKeys = new[]
    {
        AllowLinksKey, AllowModLinksKey, AllowAtAllKey, AllowLongMessagesKey, MaxLongMessageCharsKey,
    };

    /// <summary>The row id ServerSettings and the bridge use: <c>progenitor/ServerChat/&lt;key&gt;</c>.</summary>
    public static string RowId(string key) => ConfigSync.FrameworkSettingsId + "/" + Section + "/" + key;

    // ---- server only (the server's progenitor.cfg) -------------------------------------------------------------

    public const string FilterSection = "ChatFilter";
    public const string RelaySection = "ChatRelay";
    public const string EnabledKey = "Enabled";
    public const string WordsKey = "Words";
    public const string ActionKey = "Action";
    public const string WebhookUrlKey = "WebhookUrl";
    public const string GlobalKey = "Global";
    public const string LocalKey = "Local";
    public const string ClanKey = "Clan";
    public const string WhispersKey = "Whispers";
    public const string SystemKey = "System";

    // ---- the page's words -----------------------------------------------------------------------------------

    internal const string AllowLinksLabel = "Allow clickable links";
    internal const string AllowLinksHelp =
        "Web links in chat can be clicked on Progenitor clients (they always ask before opening). Off: links show as plain text "
        + "for everyone on this server.";

    internal const string AllowModLinksLabel = "Allow mod links";
    internal const string AllowModLinksHelp =
        "Shared mod links open the mod's page in the Mods window (never an install). Off: they show as plain text.";

    internal const string AllowAtAllLabel = "Allow @all from admins";
    internal const string AllowAtAllHelp =
        "An admin's @all highlights the line for every player with Progenitor, like a mention. Off: @all is plain text.";

    internal const string AllowLongMessagesLabel = "Allow long messages";
    internal const string AllowLongMessagesHelp =
        "Players with Progenitor can send a message longer than the game's limit; it goes out in parts and is joined again on "
        + "Progenitor clients. Off: the game's limit applies.";

    internal const string MaxLongMessageLabel = "Longest long message";
    internal const string MaxLongMessageHelp = "The most characters a joined long message may have. 200 to 4000.";

    internal const string FilterLabel = "Word filter";
    internal const string FilterHelp =
        "Checks every player chat line on the server for the filtered words (whole words, any case). Players' own profanity "
        + "filter is separate and unchanged.";

    internal const string FilterWordsLabel = "Filtered words";
    internal const string FilterWordsHelp =
        "Words or short phrases, separated by commas. Whole words only, any case: \"ass\" does not match \"class\". Only admins "
        + "see this list; it is never sent to players.";

    internal const string FilterActionLabel = "When a word matches";
    internal const string FilterReplace = "Replace with ***";
    internal const string FilterBlock = "Block the message";
    internal const string FilterReplaceShort = "The line is sent with each filtered word as ***. The sender's own screen shows what they typed.";
    internal const string FilterBlockShort = "The line is not sent; only the sender is told why.";
    internal const string FilterActionHelp = "What happens to a chat line that holds a filtered word.";

    internal const string RelayLabel = "Discord relay";
    internal const string RelayHelp =
        "Posts the chosen chat channels to a Discord channel through a webhook, as \"Name: message\". At most one post a second; "
        + "a burst waits in a queue of 50 lines, the oldest dropped first.";

    internal const string RelayUrlLabel = "Discord webhook address";
    internal const string RelayUrlHelp =
        "The webhook's address from Discord (channel settings, Integrations, Webhooks): https://discord.com/api/webhooks/... "
        + "Kept on the server and never shown again: this row reads Set or Not set. Empty turns the relay off.";

    internal const string RelayGlobalLabel = "Relay Global chat";
    internal const string RelayLocalLabel = "Relay Local chat";
    internal const string RelayClanLabel = "Relay clan chat";
    internal const string RelayWhispersLabel = "Relay whispers";
    internal const string RelaySystemLabel = "Relay System lines";
    internal const string RelayChannelHelp = "Posts this channel to Discord while the relay is on.";
    internal const string RelayPrivateHelp =
        "Posts this channel to Discord while the relay is on. Players expect it to be private: turn it on only with their agreement.";
    internal const string RelaySystemHelp =
        "Posts admins' System lines and server-wide notices from Progenitor and its mods to Discord while the relay is on. "
        + "Replies sent to one player are never posted.";

    /// <summary>The server-only rows' operations, in page order.</summary>
    internal static readonly IReadOnlyList<string> AdminOps = new[]
    {
        ServerOptionOps.ChatFilter, ServerOptionOps.ChatFilterWords, ServerOptionOps.ChatFilterAction,
        ServerOptionOps.ChatRelay, ServerOptionOps.ChatRelayUrl, ServerOptionOps.ChatRelayGlobal, ServerOptionOps.ChatRelayLocal,
        ServerOptionOps.ChatRelayClan, ServerOptionOps.ChatRelayWhispers, ServerOptionOps.ChatRelaySystem,
    };

    /// <summary>Each server-only option's default (the server binds them with these).</summary>
    internal static string DefaultOf(string op) => op switch
    {
        ServerOptionOps.ChatFilter => "false",
        ServerOptionOps.ChatFilterWords => "",
        ServerOptionOps.ChatFilterAction => Progenitor.Chat.ChatWordFilter.ActionReplace,
        ServerOptionOps.ChatRelay => "false",
        ServerOptionOps.ChatRelayUrl => "",
        ServerOptionOps.ChatRelayGlobal => "true",
        ServerOptionOps.ChatRelaySystem => "true",
        _ => "false",
    };
}

/// <summary>
/// CHAT4: the Server tab's Chat rows that travel on the admin channel (shown only to an admin of the joined server, like every
/// other admin-channel row). Game-free over <see cref="JoinAdminRows"/>' seams.
/// </summary>
internal static class ServerChatRows
{
    private static AdminSettingSpec Spec(string op, AdminSettingKind kind, string label, string help) => new()
    {
        SettingId = SettingPlaces.JoinAdminId(op),
        Key = op,
        Label = label,
        Help = help,
        Kind = kind,
        Read = () => JoinAdminRows.Value(op),
        Send = (v, done) => JoinAdminRows.Send(op, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(op)),
    };

    internal static AdminSettingSpec Filter() =>
        Spec(ServerOptionOps.ChatFilter, AdminSettingKind.Toggle, ServerChatOptions.FilterLabel, ServerChatOptions.FilterHelp);

    internal static AdminSettingSpec FilterWords() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.ChatFilterWords),
        Key = ServerOptionOps.ChatFilterWords,
        Label = ServerChatOptions.FilterWordsLabel,
        Help = ServerChatOptions.FilterWordsHelp,
        Kind = AdminSettingKind.Text,
        MaxLength = Progenitor.Chat.ChatWordFilter.MaxListChars,
        Read = () => JoinAdminRows.Value(ServerOptionOps.ChatFilterWords),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.ChatFilterWords, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.ChatFilterWords)),
        Display = WordsDisplay,
    };

    internal static AdminSettingSpec FilterAction() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.ChatFilterAction),
        Key = ServerOptionOps.ChatFilterAction,
        Label = ServerChatOptions.FilterActionLabel,
        Help = ServerChatOptions.FilterActionHelp,
        Kind = AdminSettingKind.Choice,
        Choices = Progenitor.Chat.ChatWordFilter.ActionWords,
        ChoiceLabels = new[] { ServerChatOptions.FilterReplace, ServerChatOptions.FilterBlock },
        ChoiceRadio = true,
        ChoiceDescriptions = new[] { ServerChatOptions.FilterReplaceShort, ServerChatOptions.FilterBlockShort },
        Read = () => JoinAdminRows.Value(ServerOptionOps.ChatFilterAction),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.ChatFilterAction, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.ChatFilterAction)),
    };

    internal static AdminSettingSpec Relay() =>
        Spec(ServerOptionOps.ChatRelay, AdminSettingKind.Toggle, ServerChatOptions.RelayLabel, ServerChatOptions.RelayHelp);

    /// <summary>Write-only: the field never shows the address, the row reads Set or Not set, and every Apply is sent.</summary>
    internal static AdminSettingSpec RelayUrl() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.ChatRelayUrl),
        Key = ServerOptionOps.ChatRelayUrl,
        Label = ServerChatOptions.RelayUrlLabel,
        Help = ServerChatOptions.RelayUrlHelp,
        Kind = AdminSettingKind.Text,
        MaxLength = Progenitor.Chat.ChatRelayFormat.MaxUrlChars,
        WriteOnly = true,
        Read = () => JoinAdminRows.Value(ServerOptionOps.ChatRelayUrl),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.ChatRelayUrl, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.ChatRelayUrl)),
        Display = SecretDisplay,
    };

    internal static AdminSettingSpec RelayGlobal() =>
        Spec(ServerOptionOps.ChatRelayGlobal, AdminSettingKind.Toggle, ServerChatOptions.RelayGlobalLabel, ServerChatOptions.RelayChannelHelp);

    internal static AdminSettingSpec RelayLocal() =>
        Spec(ServerOptionOps.ChatRelayLocal, AdminSettingKind.Toggle, ServerChatOptions.RelayLocalLabel, ServerChatOptions.RelayChannelHelp);

    internal static AdminSettingSpec RelayClan() =>
        Spec(ServerOptionOps.ChatRelayClan, AdminSettingKind.Toggle, ServerChatOptions.RelayClanLabel, ServerChatOptions.RelayPrivateHelp);

    internal static AdminSettingSpec RelayWhispers() =>
        Spec(ServerOptionOps.ChatRelayWhispers, AdminSettingKind.Toggle, ServerChatOptions.RelayWhispersLabel, ServerChatOptions.RelayPrivateHelp);

    internal static AdminSettingSpec RelaySystem() =>
        Spec(ServerOptionOps.ChatRelaySystem, AdminSettingKind.Toggle, ServerChatOptions.RelaySystemLabel, ServerChatOptions.RelaySystemHelp);

    /// <summary>Every server-only row, in page order.</summary>
    internal static IEnumerable<AdminSettingSpec> All()
    {
        yield return Filter();
        yield return FilterWords();
        yield return FilterAction();
        yield return Relay();
        yield return RelayUrl();
        yield return RelayGlobal();
        yield return RelayLocal();
        yield return RelayClan();
        yield return RelayWhispers();
        yield return RelaySystem();
    }

    /// <summary>The words as the row reads them: <c>None</c>, or the list.</summary>
    internal static string WordsDisplay(string? value)
    {
        string v = (value ?? "").Trim();
        return v.Length == 0 ? "None" : v;
    }

    /// <summary>The address as the row reads it: <c>Set</c> or <c>Not set</c>.</summary>
    internal static string SecretDisplay(string? value) =>
        string.Equals((value ?? "").Trim(), "set", StringComparison.OrdinalIgnoreCase) ? "Set" : "Not set";
}
