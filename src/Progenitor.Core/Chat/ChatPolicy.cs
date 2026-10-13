using System;

namespace Progenitor.Chat;

/// <summary>
/// CHAT4: what the joined server allows in chat, as this client knows it. Game-free.
///
/// <para><b>Both sides.</b> On a server, these come from its own <c>[Chat]</c> server settings (the Server tab's Chat
/// group, admin-edited). On a client, from the joined server's values as they reach it; with a server that has no
/// Progenitor (or before its values arrive) the client uses <see cref="Defaults"/>, so every feature that works on the
/// client alone still works. A server setting turns a feature off for everyone; it never changes a player's own look.</para>
///
/// <para>Readers call <see cref="Current"/> each time; the owner of the server settings swaps it with
/// <see cref="Use"/> when values arrive or change, and <see cref="Reset"/> on disconnect and unload.</para>
/// </summary>
internal sealed class ChatPolicy
{
    /// <summary>Clickable web links in chat (the link still asks before opening).</summary>
    internal bool AllowLinks { get; init; } = true;

    /// <summary>Mod links that open the in-game mod manager.</summary>
    internal bool AllowModLinks { get; init; } = true;

    /// <summary><c>@all</c> from an admin counts as a mention for everyone.</summary>
    internal bool AllowAtAll { get; init; } = true;

    /// <summary>Long messages sent in parts and joined on Progenitor clients.</summary>
    internal bool AllowLongMessages { get; init; } = true;

    /// <summary>The longest joined message, in characters.</summary>
    internal int MaxLongMessageChars { get; init; } = DefaultMaxLongMessageChars;

    internal const int DefaultMaxLongMessageChars = 1000;
    internal const int MinLongMessageChars = 200;
    internal const int MaxLongMessageCharsCap = 4000;

    /// <summary>What a client uses with no word from the server.</summary>
    internal static readonly ChatPolicy Defaults = new();

    private static volatile ChatPolicy _current = Defaults;

    /// <summary>The policy in force now. Never null.</summary>
    internal static ChatPolicy Current => _current;

    /// <summary>Replace the policy (the server settings owner). Null resets to <see cref="Defaults"/>.</summary>
    internal static void Use(ChatPolicy? policy) => _current = policy ?? Defaults;

    internal static void Reset() => _current = Defaults;

    internal static int ClampLongMessage(int chars) =>
        chars < MinLongMessageChars ? MinLongMessageChars : chars > MaxLongMessageCharsCap ? MaxLongMessageCharsCap : chars;
}
