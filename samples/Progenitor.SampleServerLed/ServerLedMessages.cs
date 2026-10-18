using Progenitor.Networking;

namespace Progenitor.SampleServerLed;

/// <summary>The only sample-owned message is the optional popup announcement.
/// Personal preferences use Core's scoped settings protocol.</summary>
internal static class ServerLedMessages
{
    internal const string Announce = "announce";
    internal const string FeaturePopups = "popups";
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
