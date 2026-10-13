using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Plugins;

namespace Progenitor.SampleClientLed;

/// <summary>
/// The client-led sample's handshake declaration, as constants. <c>Plugin</c>'s attributes use
/// exactly these, so the tests that compute the join matrix from them test the shipped declaration.
/// </summary>
internal static class ClientLedDeclaration
{
    /// <summary>The plugin GUID (the same on both sides: one DLL, one handshake row).</summary>
    internal const string Guid = "progenitor.sampleclientled";

    /// <summary>The display name.</summary>
    internal const string Name = "Client-led sample";

    /// <summary>
    /// Declared <c>NoNeedForSync</c> on purpose, not <c>ServerMustHave</c>: one attribute speaks for
    /// both sides, and on the server <c>[Counterpart(Limited)]</c> does not lower a
    /// <c>ServerMustHave</c> row (only <c>Optional</c> or <c>ClientExtras</c> do), so a server running
    /// the tiny server part would then refuse every client without the client part. Neither side
    /// needs the other; two sides that both run it must agree on major.minor.
    /// </summary>
    internal const NetworkCompatibilityLevel Level = NetworkCompatibilityLevel.NoNeedForSync;

    /// <summary>
    /// The plugin version: a literal semantic version, not the repository's date version, so the
    /// compatibility contract (a new field or feature is a minor version, a removed or retyped one a
    /// major version) gives <see cref="Peers"/> a meaning.
    /// </summary>
    internal const string Version = "1.0.0";

    /// <summary>
    /// The peer versions this build works with (mod compatibility C3): every 1.x. Honest because the
    /// two parts talk only through versioned messages that skip unknown fields, and the client asks
    /// only a server part that offers <see cref="ClientLedMessages.FeatureTime"/>. The newer side's
    /// range decides; a Progenitor older than ranges uses <see cref="Rule"/>.
    /// </summary>
    internal const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>For a peer whose Progenitor predates ranges: two sides that both run the mod must agree on major.minor.</summary>
    internal const NetworkVersionRule Rule = NetworkVersionRule.Minor;

    /// <summary>The client part runs without the server part, with some features unavailable.</summary>
    internal const CounterpartNeed Need = CounterpartNeed.Limited;

    /// <summary>The features that need the server part (shown on the mod page).</summary>
    internal const string FeatureServerTime = "The server's in-game time and next sunrise or sunset";

    /// <summary>The second feature that needs the server part.</summary>
    internal const string FeatureServerDefault = "The server's default clock format";
}

/// <summary>
/// The game-free rules of the client-led sample: clock formatting, the effective clock format, the
/// time request's admission, the estimate the client advances between answers,
/// and every line the sample writes. <c>Plugin</c> only wires these to the game. Linked into
/// the test project.
/// </summary>
internal static class ClientLedPolicy
{
    /// <summary>The fewest seconds between two answered requests from one player.</summary>
    internal const double ServerMinSeconds = 1.0;

    /// <summary>The client asks again this often while joined to a server with the server part.</summary>
    internal const double RefreshSeconds = 30.0;

    /// <summary>The fewest seconds between two requests the client sends (a key press included).</summary>
    internal const double ClientMinSeconds = 2.0;

    /// <summary>How long a key press waits for a fresh answer before it shows what it has.</summary>
    internal const double PopupWaitSeconds = 1.5;

    /// <summary>Clock format choices as stored in the cfg.</summary>
    internal const string FormatServerDefault = "Server default", Format24 = "24-hour", Format12 = "12-hour";

    /// <summary>The client's choices (the Radio's options).</summary>
    internal static readonly string[] ClientFormats = { FormatServerDefault, Format24, Format12 };

    /// <summary>The server's choices for its default.</summary>
    internal static readonly string[] ServerFormats = { Format24, Format12 };

    /// <summary>
    /// Why a time request was refused, or that it was accepted. The envelope already names this mod
    /// and the codec skipped what this version does not know, so what is left is the handshake and the rate.
    /// </summary>
    internal enum Verdict
    {
        Ok = 0,
        NotHandshaken = 1,
        TooSoon = 2,
    }

    /// <summary>The server part's whole validation of one request, cheapest first. A refusal sends nothing.</summary>
    internal static Verdict Admit(string? answerStatus, double? secondsSinceLast)
    {
        if (!string.Equals(answerStatus, "ok", StringComparison.Ordinal)) return Verdict.NotHandshaken;
        if (secondsSinceLast is { } since && since < ServerMinSeconds) return Verdict.TooSoon;
        return Verdict.Ok;
    }

    /// <summary>
    /// The client's decision, once a second: ask when the server part offers
    /// <see cref="ClientLedMessages.FeatureTime"/> (<c>ctx.Peer.Has("time")</c>), the join is accepted,
    /// the last request is at least <see cref="ClientMinSeconds"/> old, and either there is no answer
    /// yet, it is older than <see cref="RefreshSeconds"/>, or a key press wants a fresh one. Nothing is
    /// sent to a server whose part does not answer.
    /// </summary>
    internal static bool ShouldRequest(
        bool serverAnswers, bool joinAccepted, double secondsSinceRequest, double? secondsSinceAnswer, bool pressWaiting)
    {
        if (!serverAnswers || !joinAccepted) return false;
        if (secondsSinceRequest < ClientMinSeconds) return false;
        return pressWaiting || secondsSinceAnswer is not { } age || age >= RefreshSeconds;
    }

    /// <summary>
    /// The format the clock uses. The player's own choice wins; <c>Server default</c> takes the server
    /// part's value while it runs, and is 24-hour otherwise.
    /// </summary>
    internal static string EffectiveFormat(string? clientChoice, CounterpartPresence presence, string? serverDefault)
    {
        string choice = Normalize(clientChoice, FormatServerDefault);
        if (choice != FormatServerDefault) return choice;
        return presence == CounterpartPresence.Running ? Normalize(serverDefault, Format24, allowServerDefault: false) : Format24;
    }

    private static string Normalize(string? value, string fallback, bool allowServerDefault = true)
    {
        string v = (value ?? "").Trim();
        if (string.Equals(v, Format24, StringComparison.OrdinalIgnoreCase)) return Format24;
        if (string.Equals(v, Format12, StringComparison.OrdinalIgnoreCase)) return Format12;
        if (allowServerDefault && string.Equals(v, FormatServerDefault, StringComparison.OrdinalIgnoreCase)) return FormatServerDefault;
        return fallback;
    }

    /// <summary>A wall-clock time in the given format.</summary>
    internal static string FormatLocal(DateTime time, string format, bool seconds) =>
        FormatClock(time.Hour, time.Minute, time.Second, format, seconds);

    /// <summary>An in-game hour (23.5 is 23:30) in the given format, without seconds.</summary>
    internal static string FormatGameHour(double hour, string format)
    {
        double h = ((hour % 24) + 24) % 24;
        int totalMinutes = (int)Math.Floor(h * 60 + 1e-6) % (24 * 60);
        return FormatClock(totalMinutes / 60, totalMinutes % 60, 0, format, seconds: false);
    }

    private static string FormatClock(int hour, int minute, int second, string format, bool seconds)
    {
        string tail = ":" + minute.ToString("00", CultureInfo.InvariantCulture)
            + (seconds ? ":" + second.ToString("00", CultureInfo.InvariantCulture) : "");
        if (format != Format12) return hour.ToString("00", CultureInfo.InvariantCulture) + tail;
        int h12 = hour % 12 == 0 ? 12 : hour % 12;
        return h12.ToString(CultureInfo.InvariantCulture) + tail + (hour < 12 ? " AM" : " PM");
    }

    /// <summary>
    /// The world clock <paramref name="elapsedSeconds"/> real seconds after an answer, advanced at the
    /// answer's day length. An estimate between answers: an override or a paused clock on the server
    /// shows at the next answer.
    /// </summary>
    internal static (long Day, double Hour) Advance(TimeAnswer answer, double elapsedSeconds)
    {
        double dayLength = answer.DayLengthSeconds > 0 ? answer.DayLengthSeconds : 1800;
        double hours = answer.Hour + Math.Max(0, elapsedSeconds) * 24.0 / dayLength;
        long days = (long)Math.Floor(hours / 24.0);
        return (answer.Day + days, hours - days * 24.0);
    }

    /// <summary>
    /// The next sunrise or sunset after <paramref name="hour"/>, and how many real minutes away it is
    /// at this day length.
    /// </summary>
    internal static (string Event, double RealMinutes) NextEvent(double hour, double sunrise, double sunset, double dayLengthSeconds)
    {
        bool day = hour >= sunrise && hour < sunset;
        double target = day ? sunset : sunrise;
        double gameHours = ((target - hour) % 24 + 24) % 24;
        double dayLength = dayLengthSeconds > 0 ? dayLengthSeconds : 1800;
        return (day ? "Sunset" : "Sunrise", gameHours / 24.0 * dayLength / 60.0);
    }

    /// <summary>The "In-game time" line from an answer and its age.</summary>
    internal static string ServerTimeLine(TimeAnswer answer, double ageSeconds, string format)
    {
        var (day, hour) = Advance(answer, ageSeconds);
        var (evt, minutes) = NextEvent(hour, answer.SunriseHour, answer.SunsetHour, answer.DayLengthSeconds);
        return "Day " + day.ToString(CultureInfo.InvariantCulture) + ", " + FormatGameHour(hour, format)
            + " · " + evt.ToLowerInvariant() + " in " + Minutes(minutes);
    }

    private static string Minutes(double minutes)
    {
        int m = (int)Math.Ceiling(Math.Max(0, minutes));
        return m < 1 ? "under a minute" : m.ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>The fallback line when the server part is not running (the needs-server-part fallback in words).</summary>
    internal const string LocalEstimateLine = "Uses your local estimate: this server does not send its in-game time.";

    /// <summary>The popup title.</summary>
    internal const string PopupTitle = "Clock";

    /// <summary>The popup body: the local time, then the server's time or the fallback line.</summary>
    internal static string PopupBody(string localTime, string? serverTime, bool showServerTime)
    {
        string body = "Local time " + localTime + ".";
        if (!showServerTime) return body;
        return body + "\n" + (serverTime != null ? "In-game: " + serverTime + "." : LocalEstimateLine);
    }

    /// <summary>The mod page's live "Server part" line.</summary>
    internal static string PresenceText(CounterpartPresence presence) => presence switch
    {
        CounterpartPresence.Running => "Running on this server",
        CounterpartPresence.Absent => "Not on this server",
        _ => "Unknown (not joined, or the server did not say)",
    };

    /// <summary>The server's line for a refused request.</summary>
    internal static string RefusedLine(string? sender, Verdict verdict) =>
        "Client-led sample: refused a time request from " + (string.IsNullOrEmpty(sender) ? "<unknown>" : sender)
        + " reason=" + verdict + ".";

    /// <summary>
    /// The server part's answer bookkeeping, keyed on <c>User.Index</c> (a recycled slot: the plugin
    /// forgets a row on <c>PlayerDisconnected</c>).
    /// </summary>
    internal sealed class RequestTable
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, double> _last = new();

        /// <summary>Seconds since this player's last answered request, or null.</summary>
        internal double? SecondsSince(int userIndex, double nowSeconds)
        {
            lock (_gate) return _last.TryGetValue(userIndex, out double at) ? nowSeconds - at : null;
        }

        /// <summary>Record an answered request.</summary>
        internal void Note(int userIndex, double nowSeconds)
        {
            lock (_gate) _last[userIndex] = nowSeconds;
        }

        /// <summary>Drop a player's row (disconnect). Returns whether there was one.</summary>
        internal bool Forget(int userIndex)
        {
            lock (_gate) return _last.Remove(userIndex);
        }

        /// <summary>Rows held.</summary>
        internal int Count
        {
            get { lock (_gate) return _last.Count; }
        }

        /// <summary>Forget everything (unload).</summary>
        internal void Clear()
        {
            lock (_gate) _last.Clear();
        }
    }
}
