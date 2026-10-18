using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Plugins;

namespace Progenitor.SampleServerLed;

/// <summary>
/// The server-led sample's handshake declaration, as constants. <c>Plugin</c>'s attributes use
/// exactly these, so the tests that compute the join matrix from them test the shipped declaration.
/// </summary>
internal static class ServerLedDeclaration
{
    /// <summary>The plugin GUID (the same on both sides: one DLL, one handshake row).</summary>
    internal const string Guid = "progenitor.sampleserverled";

    /// <summary>The display name.</summary>
    internal const string Name = "Server-led sample";

    /// <summary>
    /// The declared level. <see cref="Need"/> lowers it to <c>NoNeedForSync</c> on both sides
    /// (<c>CounterpartPolicy.EffectiveLevel</c>): the server never requires the client part, and the
    /// client part never requires the server part.
    /// </summary>
    internal const NetworkCompatibilityLevel Level = NetworkCompatibilityLevel.ServerMustHave;

    /// <summary>
    /// The plugin version: a literal semantic version, not the repository's date version, so the
    /// compatibility contract (a new field or feature is a minor version, a removed or retyped one a
    /// major version) gives <see cref="Peers"/> a meaning.
    /// </summary>
    internal const string Version = "1.0.0";

    /// <summary>
    /// The optional popup protocol stays compatible with 1.x clients. Preference editing needs
    /// a Core with scoped settings: the old sample-owned prefs feature is no longer offered.
    /// Each side gates popup messages on the client's advertised feature.
    /// </summary>
    internal const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>For a peer whose Progenitor predates ranges: two sides that both run the mod must agree on major.minor.</summary>
    internal const NetworkVersionRule Rule = NetworkVersionRule.Minor;

    /// <summary>The server part works fully without the client part; the client part only adds extras.</summary>
    internal const CounterpartNeed Need = CounterpartNeed.Optional;

    /// <summary>What the optional client part adds (shown on the mod page; also lowers the server's row).</summary>
    internal const string ClientExtras =
        "Announcements shown as a popup instead of a chat line, with local popup controls.";

    /// <summary>The features that need the server part, for the client's mod page.</summary>
    internal const string FeatureAnnouncements = "Server announcements";
}

/// <summary>
/// The game-free rules of the server-led sample: reads by stable platform account, how an
/// announcement reaches each player, the schedule and text. <c>Plugin</c> only wires these to
/// the game. Linked into the test project.
/// </summary>
internal static class ServerLedPolicy
{
    /// <summary>The longest announcement text the client part shows; longer text is cut (the server never sends one).</summary>
    internal const int MaxPopupChars = 480;

    /// <summary>How often the server's announcement loop checks whether an announcement is due.</summary>
    internal const double TickSeconds = 5.0;

    /// <summary>Announcement interval bounds, in minutes.</summary>
    internal const int MinMinutes = 1, MaxMinutes = 120, DefaultMinutes = 10;

    /// <summary>Popup auto-close bounds, in seconds (0 keeps the popup until OK is pressed).</summary>
    internal const int MinPopupSeconds = 0, MaxPopupSeconds = 60, DefaultPopupSeconds = 8;

    /// <summary>The longest greeting an announcement carries; longer text is cut.</summary>
    internal const int MaxGreetingChars = 160;

    /// <summary>The default server greeting.</summary>
    internal const string DefaultGreeting = "Welcome. This server runs the server-led sample.";

    /// <summary>Delivery choices persisted separately for each player on the server.</summary>
    internal const string DeliveryChat = "Chat", DeliveryPopup = "Popup";

    /// <summary>Delivery choices in order (the Radio's options).</summary>
    internal static readonly string[] DeliveryChoices = { DeliveryChat, DeliveryPopup };

    /// <summary>
    /// The text a received announcement shows as a popup: the chat line's exact text, which the server
    /// put in <see cref="AnnounceMessage.Text"/>; null for an empty one (nothing to show). A text longer
    /// than <see cref="MaxPopupChars"/> is cut and ends with an ellipsis.
    /// </summary>
    internal static string? PopupText(AnnounceMessage? message)
    {
        string text = (message?.Text ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.Length <= MaxPopupChars) return text;
        int cut = MaxPopupChars - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut) + "…";
    }

    /// <summary>Plain values read from the two server-owned PlayerSetting handles.</summary>
    internal readonly struct PlayerPrefs
    {
        internal PlayerPrefs(bool receive, string delivery) { Receive = receive; Delivery = delivery; }
        internal bool Receive { get; }
        internal string Delivery { get; }
    }

    /// <summary>Read both settings by stable account, never by a recycled connection slot.
    /// An unidentified player uses defaults without attempting a persisted lookup.</summary>
    internal static PlayerPrefs PreferencesFor(ulong platformId, Func<ulong, bool> receive, Func<ulong, string> delivery) =>
        platformId == 0 ? new PlayerPrefs(true, DeliveryChat) : new PlayerPrefs(receive(platformId), delivery(platformId));

    /// <summary>How one announcement reaches one player.</summary>
    internal enum Route
    {
        /// <summary>The player turned announcements off.</summary>
        Skip = 0,

        /// <summary>A system chat line from the server (every player without the client part).</summary>
        Chat = 1,

        /// <summary>The announcement message; the client part shows it as a popup.</summary>
        Popup = 2,
    }

    /// <summary>Receive=false survives client unload; Popup falls back to chat when its
    /// client feature is absent. The preference itself remains saved for the next session.</summary>
    internal static Route RouteFor(in PlayerPrefs prefs, bool clientShowsPopups)
    {
        if (!prefs.Receive) return Route.Skip;
        return prefs.Delivery == DeliveryPopup && clientShowsPopups ? Route.Popup : Route.Chat;
    }

    /// <summary>The interval the server uses: the cfg value clamped to its bounds, in seconds.</summary>
    internal static double IntervalSeconds(int minutes) =>
        Math.Clamp(minutes, MinMinutes, MaxMinutes) * 60.0;

    /// <summary>True when an announcement is due.</summary>
    internal static bool IsDue(double nowSeconds, double lastAnnounceSeconds, int minutes) =>
        nowSeconds - lastAnnounceSeconds >= IntervalSeconds(minutes);

    /// <summary>The greeting as an announcement uses it: trimmed, one line, at most <see cref="MaxGreetingChars"/>.</summary>
    internal static string CleanGreeting(string? greeting)
    {
        string text = (greeting ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return "Server announcement.";
        return text.Length <= MaxGreetingChars ? text : text.Substring(0, MaxGreetingChars - 1) + "…";
    }

    /// <summary>The announcement text, identical as a chat line and as a popup body.</summary>
    internal static string AnnouncementText(string? greeting, int playersOnline, int uptimeMinutes) =>
        CleanGreeting(greeting) + " · " + N(Math.Max(0, playersOnline)) + (playersOnline == 1 ? " player" : " players")
        + " online · server up " + Uptime(uptimeMinutes) + ".";

    /// <summary><c>42 min</c> or <c>3 h 5 min</c>.</summary>
    internal static string Uptime(int minutes)
    {
        minutes = Math.Max(0, minutes);
        return minutes < 60 ? N(minutes) + " min" : N(minutes / 60) + " h " + N(minutes % 60) + " min";
    }

    /// <summary>The popup's title.</summary>
    internal const string PopupTitle = "Server announcement";

    // ---- lines ------------------------------------------------------------------------------------

    /// <summary>The mod page's live "Server part" line.</summary>
    internal static string PresenceText(CounterpartPresence presence) => presence switch
    {
        CounterpartPresence.Running => "Running on this server",
        CounterpartPresence.Absent => "Not on this server",
        _ => "Unknown (not joined, or the server did not say)",
    };

    /// <summary>
    /// Whether to write the join-check line now: once per join, the first time the server part's state
    /// is known. <paramref name="joinKey"/> identifies the join (the client world's sequence number,
    /// <c>ClientWorldEpoch.ObservedSequence</c>; 0 = none), and <paramref name="lastLogged"/> is the key
    /// of the join already logged, kept process-wide by the plugin so a Core swap or a plugin reload
    /// (which blip the state to Unknown and back) does not log the same join twice.
    /// </summary>
    internal static bool ShouldLogJoinCheck(ulong joinKey, CounterpartPresence presence, string? lastLogged, out string key)
    {
        key = joinKey.ToString(CultureInfo.InvariantCulture);
        if (joinKey == 0 || presence == CounterpartPresence.Unknown) return false;
        return !string.Equals(lastLogged, key, StringComparison.Ordinal);
    }

    /// <summary>The client's join-check log line, once per join when the server part's state becomes known; null while unknown.</summary>
    internal static string? JoinCheckLine(CounterpartPresence presence) => presence switch
    {
        CounterpartPresence.Running => "Server-led sample: this server runs the server part; it announces by your preferences.",
        CounterpartPresence.Absent => "Server-led sample: this server does not run the server part, so it announces nothing.",
        _ => null,
    };

    /// <summary>The server's line for one announcement.</summary>
    internal static string AnnouncedLine(int sequence, int chat, int popup, int skipped) =>
        "Server-led sample: announcement " + N(sequence) + " sent: chat=" + N(chat) + " popup=" + N(popup)
        + " skipped=" + N(skipped) + ".";

    /// <summary><c>.serverled status</c>: what the server does, and what it does for the caller.</summary>
    internal static IReadOnlyList<string> StatusLines(int minutes, string? greeting, in PlayerPrefs caller) => new[]
    {
        "Server-led sample: an announcement every " + N(Math.Clamp(minutes, MinMinutes, MaxMinutes)) + " min: \""
            + CleanGreeting(greeting) + "\".",
        "Your preferences: receive=" + OnOff(caller.Receive) + ", delivery=" + caller.Delivery
            + " (server-owned per-account settings; defaults apply until changed).",
    };

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
