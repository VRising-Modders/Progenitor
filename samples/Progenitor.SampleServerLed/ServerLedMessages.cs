using Progenitor.Networking;

namespace Progenitor.SampleServerLed;

// The server-led sample's wire contract: its two versioned messages (ctx.Messages) and the features each part offers
// (ctx.Features). Game-free on purpose, with no using but Progenitor.Networking: test projects link this file as
// source (tests/Progenitor.Testing.Tests replays recorded fixtures through it). Under the compatibility contract a
// new field or feature is a minor version, a removed or retyped one a major version.

/// <summary>The message and feature names of the server-led sample, and the values its fields carry.</summary>
internal static class ServerLedMessages
{
    /// <summary>Message name of <see cref="PrefsMessage"/>.</summary>
    internal const string Prefs = "prefs";

    /// <summary>Message name of <see cref="AnnounceMessage"/>.</summary>
    internal const string Announce = "announce";

    /// <summary>Offered by the server part: it reads <see cref="PrefsMessage"/> and honors it per player.</summary>
    internal const string FeaturePrefs = "prefs";

    /// <summary>Offered by the client part: it shows <see cref="AnnounceMessage"/> as a popup.</summary>
    internal const string FeaturePopups = "popups";

    /// <summary><see cref="PrefsMessage.Kind"/>: this player's preferences.</summary>
    internal const int KindPrefs = 0;

    /// <summary><see cref="PrefsMessage.Kind"/>: the client part is going away (unload); use the server defaults.</summary>
    internal const int KindGone = 1;

    /// <summary><see cref="PrefsMessage.Delivery"/>: a system chat line.</summary>
    internal const int WireChat = 0;

    /// <summary><see cref="PrefsMessage.Delivery"/>: the announcement message, shown by the client part as a popup.</summary>
    internal const int WirePopup = 1;

    /// <summary>
    /// The values this version understands. A field a newer client adds is skipped by the codec; a value of a known
    /// field this version does not define (a new kind or delivery) is refused rather than guessed at, so that player
    /// keeps the server defaults.
    /// </summary>
    internal static bool IsValid(PrefsMessage? message) =>
        message != null
        && message.Kind is KindPrefs or KindGone
        && (message.Kind == KindGone || message.Delivery is WireChat or WirePopup);
}

/// <summary>
/// Client to server: this player's preferences (<see cref="ServerLedMessages.KindPrefs"/>), or the goodbye the client part
/// sends when it unloads (<see cref="ServerLedMessages.KindGone"/>). Version 1, written by 1.0.0.
/// </summary>
[ModMessage(ServerLedMessages.Prefs)]
internal sealed class PrefsMessage
{
    /// <summary><see cref="ServerLedMessages.KindPrefs"/> or <see cref="ServerLedMessages.KindGone"/>.</summary>
    [Field(1)] public int Kind = ServerLedMessages.KindPrefs;

    /// <summary>The player wants announcements.</summary>
    [Field(2)] public bool Receive = true;

    /// <summary><see cref="ServerLedMessages.WireChat"/> or <see cref="ServerLedMessages.WirePopup"/>.</summary>
    [Field(3)] public int Delivery = ServerLedMessages.WireChat;
}

/// <summary>Server to client: one announcement, with the chat line's exact text. Version 1, written by 1.0.0.</summary>
[ModMessage(ServerLedMessages.Announce)]
internal sealed class AnnounceMessage
{
    /// <summary>The server's announcement counter (since its part loaded).</summary>
    [Field(1)] public int Sequence;

    /// <summary>The text the chat line carries; the popup shows exactly this.</summary>
    [Field(2)] public string Text = "";
}
