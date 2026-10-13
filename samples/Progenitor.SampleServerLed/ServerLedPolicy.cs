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
    /// The peer versions this build works with (mod compatibility C3): every 1.x. Honest because the
    /// two parts talk only through versioned messages that skip unknown fields and gate every send on
    /// the other part's offered feature. The newer side's range decides; a Progenitor older than
    /// ranges uses <see cref="Rule"/>.
    /// </summary>
    internal const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>For a peer whose Progenitor predates ranges: two sides that both run the mod must agree on major.minor.</summary>
    internal const NetworkVersionRule Rule = NetworkVersionRule.Minor;

    /// <summary>The server part works fully without the client part; the client part only adds extras.</summary>
    internal const CounterpartNeed Need = CounterpartNeed.Optional;

    /// <summary>What the optional client part adds (shown on the mod page; also lowers the server's row).</summary>
    internal const string ClientExtras =
        "Per-player announcement preferences, and announcements shown as a popup instead of a chat line.";

    /// <summary>The features that need the server part, for the client's mod page.</summary>
    internal const string FeatureAnnouncements = "Server announcements";
}

/// <summary>
/// The game-free rules of the server-led sample: the preference message and its validation, the
/// per-player preference table the server keeps, how an announcement reaches each player, the
/// announcement schedule and every line the sample writes. <c>Plugin</c> only wires these to
/// the game. Linked into the test project.
/// </summary>
internal static class ServerLedPolicy
{
    /// <summary><see cref="PrefsMessage.Kind"/>: this player's preferences.</summary>
    internal const int KindPrefs = ServerLedMessages.KindPrefs;

    /// <summary><see cref="PrefsMessage.Kind"/>: the client part is going away (unload); use the server defaults.</summary>
    internal const int KindGone = ServerLedMessages.KindGone;

    /// <summary>The fewest seconds between two accepted preference messages from one player.</summary>
    internal const double ServerMinSeconds = 2.0;

    /// <summary>The fewest seconds between two preference messages the client sends.</summary>
    internal const double ClientMinSeconds = 3.0;

    /// <summary>
    /// The client re-sends its preferences this often while joined, so a server whose sample was
    /// reloaded (and so forgot every preference) learns them again within a minute.
    /// </summary>
    internal const double HeartbeatSeconds = 60.0;

    /// <summary>
    /// Preferences older than this (two and a half heartbeats) no longer count: the client part was
    /// switched off, failed to reload or lost its connection without saying goodbye, so the server
    /// goes back to its defaults — a chat line — instead of popups nobody shows.
    /// </summary>
    internal const double StaleSeconds = HeartbeatSeconds * 2.5;

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

    /// <summary>Delivery choices as stored in the client's cfg.</summary>
    internal const string DeliveryChat = "Chat", DeliveryPopup = "Popup";

    /// <summary>Delivery choices in order (the Radio's options).</summary>
    internal static readonly string[] DeliveryChoices = { DeliveryChat, DeliveryPopup };

    /// <summary>Delivery as it travels on the wire (<see cref="PrefsMessage.Delivery"/>).</summary>
    internal const int WireChat = ServerLedMessages.WireChat, WirePopup = ServerLedMessages.WirePopup;

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

    /// <summary>One player's preferences as the server uses them.</summary>
    internal readonly struct PlayerPrefs : IEquatable<PlayerPrefs>
    {
        internal PlayerPrefs(bool receive, int delivery, bool fromClient)
        {
            Receive = receive;
            Delivery = delivery;
            FromClient = fromClient;
        }

        /// <summary>The player wants announcements.</summary>
        internal bool Receive { get; }

        /// <summary><see cref="WireChat"/> or <see cref="WirePopup"/>.</summary>
        internal int Delivery { get; }

        /// <summary>True when the player's client part sent these; false for the server defaults.</summary>
        internal bool FromClient { get; }

        public bool Equals(PlayerPrefs other) =>
            Receive == other.Receive && Delivery == other.Delivery && FromClient == other.FromClient;

        public override bool Equals(object? obj) => obj is PlayerPrefs other && Equals(other);

        public override int GetHashCode() => (Receive ? 1 : 0) | (Delivery << 1) | (FromClient ? 8 : 0);
    }

    /// <summary>What the server uses for a player whose client part has not reported: every announcement, as a chat line.</summary>
    internal static PlayerPrefs ServerDefaults => new(receive: true, WireChat, fromClient: false);

    /// <summary>
    /// Why a preference message was refused, or that it was accepted. The envelope already names this
    /// mod and the codec skipped what this version does not know, so what is left to check is the
    /// values, the handshake and the rate.
    /// </summary>
    internal enum Verdict
    {
        Ok = 0,
        BadValue = 1,
        NotHandshaken = 2,
        TooSoon = 3,
    }

    /// <summary>
    /// The server's whole validation of one preference message, cheapest first. A refusal changes
    /// nothing and sends nothing back.
    /// </summary>
    /// <param name="message">The decoded message.</param>
    /// <param name="answerStatus">The sender's handshake answer status (<c>Handshake.UserAnswerStatus</c>).</param>
    /// <param name="secondsSinceLast">Seconds since this player's last accepted message; null for none this session.</param>
    internal static Verdict Admit(PrefsMessage? message, string? answerStatus, double? secondsSinceLast)
    {
        if (message == null || !ServerLedMessages.IsValid(message)) return Verdict.BadValue;
        if (!string.Equals(answerStatus, "ok", StringComparison.Ordinal)) return Verdict.NotHandshaken;
        // A goodbye is never too soon: it may follow a heartbeat by a moment, and it only removes state.
        if (message.Kind == KindPrefs && secondsSinceLast is { } since && since < ServerMinSeconds) return Verdict.TooSoon;
        return Verdict.Ok;
    }

    /// <summary>The preferences an accepted message carries.</summary>
    internal static PlayerPrefs FromMessage(PrefsMessage message) =>
        new(message.Receive, message.Delivery, fromClient: true);

    /// <summary>The message a client sends for its current settings.</summary>
    internal static PrefsMessage ToMessage(bool receive, string? delivery) => new()
    {
        Kind = KindPrefs,
        Receive = receive,
        Delivery = WireDelivery(delivery),
    };

    /// <summary>The goodbye the client part sends when it unloads: the server forgets this player's preferences.</summary>
    internal static PrefsMessage GoneMessage() => new() { Kind = KindGone };

    /// <summary>A cfg delivery value on the wire; anything but <see cref="DeliveryPopup"/> is chat.</summary>
    internal static int WireDelivery(string? delivery) =>
        string.Equals((delivery ?? "").Trim(), DeliveryPopup, StringComparison.OrdinalIgnoreCase) ? WirePopup : WireChat;

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

    /// <summary>
    /// The server's per-player decision. A popup needs the client part, so only preferences the
    /// client part sent can choose it, and only while that player's client offers
    /// <see cref="ServerLedMessages.FeaturePopups"/> (<c>ctx.Peers.Of(user).Has("popups")</c>); every
    /// other player gets the chat line. The plugin also sends the chat line when a popup's send does not go out.
    /// </summary>
    internal static Route RouteFor(in PlayerPrefs prefs, bool clientShowsPopups)
    {
        if (!prefs.Receive) return Route.Skip;
        return prefs.FromClient && prefs.Delivery == WirePopup && clientShowsPopups ? Route.Popup : Route.Chat;
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

    // ---- the server's per-player table ------------------------------------------------------------

    /// <summary>
    /// The server's preferences per connected player, keyed on <c>User.Index</c>, which the game
    /// recycles: the plugin drops a row on <c>PlayerDisconnected</c>, so the next player in that slot
    /// starts on the server defaults. Owned by one plugin generation; a reload starts empty and the
    /// clients' heartbeat refills it.
    /// </summary>
    internal sealed class PrefsTable
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, (PlayerPrefs Prefs, double At)> _rows = new();

        /// <summary>
        /// This player's preferences at <paramref name="nowSeconds"/>, or the server defaults when the
        /// client part never reported or its last message is older than <see cref="StaleSeconds"/>.
        /// </summary>
        internal PlayerPrefs For(int userIndex, double nowSeconds)
        {
            lock (_gate)
                return _rows.TryGetValue(userIndex, out var row) && nowSeconds - row.At <= StaleSeconds
                    ? row.Prefs
                    : ServerDefaults;
        }

        /// <summary>Seconds since this player's last accepted message, or null.</summary>
        internal double? SecondsSince(int userIndex, double nowSeconds)
        {
            lock (_gate) return _rows.TryGetValue(userIndex, out var row) ? nowSeconds - row.At : null;
        }

        /// <summary>Store an accepted message's preferences. Returns true when they changed.</summary>
        internal bool Set(int userIndex, PlayerPrefs prefs, double nowSeconds)
        {
            lock (_gate)
            {
                bool changed = !_rows.TryGetValue(userIndex, out var old) || !old.Prefs.Equals(prefs);
                _rows[userIndex] = (prefs, nowSeconds);
                return changed;
            }
        }

        /// <summary>Drop a player's row (disconnect). Returns whether there was one.</summary>
        internal bool Forget(int userIndex)
        {
            lock (_gate) return _rows.Remove(userIndex);
        }

        /// <summary>Players whose client part reported preferences within <see cref="StaleSeconds"/>.</summary>
        internal int CountAt(double nowSeconds)
        {
            lock (_gate)
            {
                int n = 0;
                foreach (var row in _rows.Values) if (nowSeconds - row.At <= StaleSeconds) n++;
                return n;
            }
        }

        /// <summary>Forget everything (load and unload).</summary>
        internal void Clear()
        {
            lock (_gate) _rows.Clear();
        }
    }

    // ---- the client's send decision ---------------------------------------------------------------

    /// <summary>
    /// The client part's decision, once a second: send the current preferences when the server part
    /// offers <see cref="ServerLedMessages.FeaturePrefs"/>, the join is accepted, and either they
    /// changed since the last send (at most every <see cref="ClientMinSeconds"/>) or the heartbeat is
    /// due. Nothing is ever sent to a server whose part does not read preferences.
    /// </summary>
    /// <param name="serverTakesPrefs">The joined server's part offers the feature (<c>ctx.Peer.Has("prefs")</c>).</param>
    /// <param name="joinAccepted">The join's handshake result is <c>ok</c>.</param>
    /// <param name="current">The message for the current settings.</param>
    /// <param name="lastSent">The last message sent this join, or null for none.</param>
    /// <param name="secondsSinceSend">Seconds since that send.</param>
    internal static bool ShouldSend(
        bool serverTakesPrefs, bool joinAccepted, PrefsMessage current, PrefsMessage? lastSent, double secondsSinceSend)
    {
        if (!serverTakesPrefs || !joinAccepted) return false;
        if (lastSent is not { } last) return true;
        bool changed = last.Receive != current.Receive || last.Delivery != current.Delivery;
        if (changed) return secondsSinceSend >= ClientMinSeconds;
        return secondsSinceSend >= HeartbeatSeconds;
    }

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

    /// <summary>The server's line for a refused preference message.</summary>
    internal static string RefusedLine(string? sender, Verdict verdict) =>
        "Server-led sample: refused preferences from " + Name(sender) + " reason=" + verdict + ".";

    /// <summary>The server's line for accepted preferences.</summary>
    internal static string AcceptedLine(string? sender, in PlayerPrefs prefs, bool changed) =>
        "Server-led sample: preferences from " + Name(sender) + " receive=" + OnOff(prefs.Receive)
        + " delivery=" + DeliveryName(prefs.Delivery) + (changed ? "" : " (unchanged)") + ".";

    /// <summary>The server's line for one announcement.</summary>
    internal static string AnnouncedLine(int sequence, int chat, int popup, int skipped) =>
        "Server-led sample: announcement " + N(sequence) + " sent: chat=" + N(chat) + " popup=" + N(popup)
        + " skipped=" + N(skipped) + ".";

    /// <summary><c>.serverled status</c>: what the server does, and what it does for the caller.</summary>
    internal static IReadOnlyList<string> StatusLines(int minutes, string? greeting, in PlayerPrefs caller, int reporting) => new[]
    {
        "Server-led sample: an announcement every " + N(Math.Clamp(minutes, MinMinutes, MaxMinutes)) + " min: \""
            + CleanGreeting(greeting) + "\".",
        "Your preferences: receive=" + OnOff(caller.Receive) + ", delivery=" + DeliveryName(caller.Delivery) + " ("
            + (caller.FromClient ? "sent by your client part" : "server defaults: your client part has not reported") + ").",
        "Players whose client part reported preferences: " + N(reporting) + ".",
    };

    /// <summary>The wire delivery as a word.</summary>
    internal static string DeliveryName(int delivery) => delivery == WirePopup ? DeliveryPopup : DeliveryChat;

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Name(string? sender) => string.IsNullOrEmpty(sender) ? "<unknown>" : sender!;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
