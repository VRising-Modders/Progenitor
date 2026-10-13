using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// NOPROGNOTICE: the timing settings of the notice to a player whose client never answers the handshake (a player
/// without Progenitor). <c>Networking.NoAnswerNoticeSeconds</c>, <c>NoAnswerNoticeRepeatSeconds</c> and
/// <c>NoAnswerCreationLimitSeconds</c> in <c>progenitor.cfg</c>, with the answer deadline
/// (<c>HandshakeAnswerTimeoutSeconds</c>).
/// </summary>
internal readonly struct NoAnswerNoticeSettings
{
    internal const int DefaultNoticeSeconds = 5;
    internal const int DefaultRepeatSeconds = 3;
    internal const int MinRepeatSeconds = 2;
    internal const int DefaultCreationLimitSeconds = 600;

    public NoAnswerNoticeSettings(int timeoutSeconds, int noticeSeconds, int repeatSeconds, int creationLimitSeconds)
    {
        TimeoutSeconds = Math.Max(0, timeoutSeconds);
        // The first notice must come before the kick: at or past the deadline it is moved to one second before it,
        // and a deadline of a second or less leaves no room for one.
        int notice = Math.Max(0, noticeSeconds);
        if (notice > 0 && notice >= TimeoutSeconds) notice = TimeoutSeconds - 1;
        NoticeSeconds = Math.Max(0, notice);
        RepeatSeconds = Math.Max(MinRepeatSeconds, repeatSeconds);
        CreationLimitSeconds = Math.Max(TimeoutSeconds, creationLimitSeconds);
    }

    /// <summary>Seconds from the countdown's start to the kick (the answer deadline).</summary>
    public int TimeoutSeconds { get; }

    /// <summary>Seconds from the countdown's start to the first notice; 0: no notice.</summary>
    public int NoticeSeconds { get; }

    /// <summary>Seconds between notices (at least 2).</summary>
    public int RepeatSeconds { get; }

    /// <summary>The longest a player without a character (in character creation) holds the countdown, from the ask.</summary>
    public int CreationLimitSeconds { get; }

    public bool NoticeEnabled => NoticeSeconds > 0;
}

/// <summary>NOPROGNOTICE: one unanswered user's countdown.</summary>
internal sealed class NoAnswerNoticeState
{
    public NoAnswerNoticeState(int generation, string name, double askedAt)
    {
        Generation = generation;
        Name = name ?? "";
        AskedAt = askedAt;
    }

    public int Generation { get; }
    public string Name { get; }
    public double AskedAt { get; }

    /// <summary>When the countdown started: the ask when the player already had a character, else when one first appeared. Null while held.</summary>
    public double? CountdownStart { get; set; }

    /// <summary>The countdown waited for character creation (the player had no character at the ask).</summary>
    public bool HeldForCreation { get; set; }

    public double NextNoticeAt { get; set; } = double.NaN;
    public int NoticesSent { get; set; }

    /// <summary>How many of the notices carried the full line.</summary>
    public int FullSent { get; set; }

    /// <summary>The last full line sent.</summary>
    public string LastText { get; set; } = "";

    /// <summary>The last short line sent.</summary>
    public string LastShort { get; set; } = "";

    public List<int> SecondsShown { get; } = new();
}

/// <summary>What the countdown does now.</summary>
internal enum NoAnswerStep
{
    /// <summary>Nothing now; look again after the step's delay.</summary>
    Wait,

    /// <summary>Send the notice (with the step's seconds left), then look again after the delay.</summary>
    Notice,

    /// <summary>The deadline: the existing kick.</summary>
    Kick,
}

/// <summary>
/// NOPROGNOTICE: the countdown, game-free. While the user has no character (character creation shows no chat or
/// HUD) both the notice and the kick are held, up to <see cref="NoAnswerNoticeSettings.CreationLimitSeconds"/> from
/// the ask, after which the kick happens anyway. Once a character is in the world (or was there at the ask: today's
/// behavior), the notice comes <see cref="NoAnswerNoticeSettings.NoticeSeconds"/> after the countdown's start and
/// every <see cref="NoAnswerNoticeSettings.RepeatSeconds"/> after that, and the kick at the start plus the deadline.
/// </summary>
internal static class NoAnswerNoticePlan
{
    /// <summary>How often a held countdown looks for the character.</summary>
    internal const double HoldPollSeconds = 1.0;

    internal readonly struct Result
    {
        public Result(NoAnswerStep step, int secondsLeft, double delay)
        {
            Step = step;
            SecondsLeft = secondsLeft;
            Delay = delay;
        }

        public NoAnswerStep Step { get; }

        /// <summary>For a notice: whole seconds until the kick (rounded up).</summary>
        public int SecondsLeft { get; }

        /// <summary>When to look again, in seconds from now.</summary>
        public double Delay { get; }
    }

    internal static Result Step(NoAnswerNoticeState s, double now, bool hasCharacter, NoAnswerNoticeSettings cfg)
    {
        if (s.CountdownStart == null)
        {
            if (!hasCharacter)
            {
                s.HeldForCreation = true;
                double limitAt = s.AskedAt + cfg.CreationLimitSeconds;
                if (now >= limitAt) return new Result(NoAnswerStep.Kick, 0, 0);
                return new Result(NoAnswerStep.Wait, 0, Math.Min(HoldPollSeconds, Math.Max(0.05, limitAt - now)));
            }

            // A character at the ask: the countdown is the ask's, exactly as before. One that appeared later: from now.
            s.CountdownStart = s.HeldForCreation ? now : s.AskedAt;
            s.NextNoticeAt = s.CountdownStart.Value + cfg.NoticeSeconds;
        }

        double kickAt = s.CountdownStart.Value + cfg.TimeoutSeconds;
        if (now >= kickAt) return new Result(NoAnswerStep.Kick, 0, 0);
        if (cfg.NoticeEnabled && now >= s.NextNoticeAt)
        {
            int left = (int)Math.Ceiling(kickAt - now - 1e-9);
            if (left < 1) left = 1;
            // The next notice; notices that fell behind (a slow frame) are not sent twice.
            do s.NextNoticeAt += cfg.RepeatSeconds; while (s.NextNoticeAt <= now);
            return new Result(NoAnswerStep.Notice, left, Math.Max(0.05, Math.Min(s.NextNoticeAt, kickAt) - now));
        }

        double next = cfg.NoticeEnabled ? Math.Min(s.NextNoticeAt, kickAt) : kickAt;
        return new Result(NoAnswerStep.Wait, 0, Math.Max(0.05, next - now));
    }
}

/// <summary>NOPROGNOTICE: the notice's words.</summary>
internal static class NoAnswerNoticeText
{
    /// <summary>The admins' message at most (the Server settings row's limit).</summary>
    internal const int MaxTemplateChars = 300;

    /// <summary>Mods named before <c>and N more</c>.</summary>
    internal const int MaxListedMods = 5;

    internal const string TokenMods = "{mods}";
    internal const string TokenSeconds = "{seconds}";

    /// <summary>TOKENTIPS1: every token <see cref="Build"/> replaces (the tip lists exactly these).</summary>
    internal static readonly string[] Tokens = { TokenMods, TokenSeconds };

    /// <summary>
    /// The default names only Progenitor (decided 2026-10-05): once it is installed, its join screens offer
    /// every other mod the server needs. An admin's own text can still use <see cref="TokenMods"/> for the list.
    /// </summary>
    internal const string DefaultTemplate =
        "This server uses mods. To join, install the Progenitor mod: get it free with a mod manager, r2modman or the "
        + "Thunderstore app (thunderstore.io, game: V Rising). Progenitor sets up the rest of this server's mods when you "
        + "join again. You'll be disconnected in {seconds} seconds.";

    /// <summary>Kept for callers; the default no longer differs when the server names no mod.</summary>
    internal const string DefaultTemplateNoMods = DefaultTemplate;

    /// <summary>The short line's longest (it is drawn as one line of floating text over the player's character).</summary>
    internal const int MaxShortChars = 70;

    /// <summary>
    /// The short line, fixed format: <c>This server needs the Progenitor mod. Disconnecting in 12 s.</c> Sent as Local chat
    /// addressed to the player as the player, which the game draws over their own character (live 2026-10-05).
    /// </summary>
    internal static string Short(int seconds) =>
        "This server needs the Progenitor mod. Disconnecting in " + Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture) + " s.";

    /// <summary>The full line goes out with the first notice and every other repeat after it (the chat log has the details without a flood).</summary>
    internal static bool FullLineDue(int noticeIndex) => noticeIndex % 2 == 0;

    /// <summary>The mod list: <c>A 1.0, B 2.0, C, D, E, and 3 more</c> (the first five).</summary>
    internal static string ModsList(IReadOnlyList<string>? mods)
    {
        if (mods == null || mods.Count == 0) return "";
        var shown = new List<string>();
        for (int i = 0; i < mods.Count && shown.Count < MaxListedMods; i++)
            if (!string.IsNullOrWhiteSpace(mods[i])) shown.Add(mods[i].Trim());
        int more = 0;
        for (int i = 0; i < mods.Count; i++) if (!string.IsNullOrWhiteSpace(mods[i])) more++;
        more -= shown.Count;
        return string.Join(", ", shown) + (more > 0 ? ", and " + more.ToString(CultureInfo.InvariantCulture) + " more" : "");
    }

    /// <summary>
    /// The chat line: the admins' message (<paramref name="template"/>; empty: the default) with <c>{mods}</c> and
    /// <c>{seconds}</c> filled in. Printable ASCII only (anything else becomes <c>?</c>; <c>&lt;</c> and <c>&gt;</c>
    /// become parentheses, so the line carries no chat markup), one line, inside the chat's 509-byte cap.
    /// </summary>
    internal static string Build(string? template, IReadOnlyList<string>? mods, int seconds)
    {
        string list = ModsList(mods);
        string t = (template ?? "").Trim();
        if (t.Length > MaxTemplateChars) t = t.Substring(0, MaxTemplateChars);
        if (t.Length == 0) t = list.Length > 0 ? DefaultTemplate : DefaultTemplateNoMods;
        string text = t.Replace(TokenMods, list.Length > 0 ? list : "Progenitor")
            .Replace(TokenSeconds, Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture));
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '<') sb.Append('(');
            else if (c == '>') sb.Append(')');
            else if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
            else if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else if (c == '’' || c == '‘') sb.Append('\'');
            else if (c == '—' || c == '–') sb.Append('-');
            else sb.Append('?');
        }

        string line = sb.ToString();
        return line.Length > Chat.ChatLogic.MaxUtf8Bytes ? line.Substring(0, Chat.ChatLogic.MaxUtf8Bytes) : line;
    }
}

/// <summary>NOPROGNOTICE: one notice: the short floating line and, on alternate repeats, the full chat line.</summary>
internal sealed class NoProgenitorNotice
{
    public NoProgenitorNotice(string shortLine, string? fullLine)
    {
        Short = shortLine ?? "";
        Full = fullLine;
    }

    /// <summary>At most <see cref="NoAnswerNoticeText.MaxShortChars"/>: the line drawn over the player's character.</summary>
    public string Short { get; }

    /// <summary>The mod list and install instructions (the admins' message when set), or null on a short-only repeat.</summary>
    public string? Full { get; }

    /// <summary>The notice the countdown sends at notice number <paramref name="noticeIndex"/> (0 first) with <paramref name="seconds"/> left.</summary>
    internal static NoProgenitorNotice For(int noticeIndex, int seconds, string? template, IReadOnlyList<string>? mods) =>
        new(NoAnswerNoticeText.Short(seconds), NoAnswerNoticeText.FullLineDue(noticeIndex) ? NoAnswerNoticeText.Build(template, mods, seconds) : null);
}

/// <summary>NOPROGNOTICE: what was sent to whom (server readback; nothing is written to disk).</summary>
internal static class NoAnswerNoticeRecords
{
    internal sealed class Row
    {
        public string Name = "";
        public int Generation;
        public bool HeldForCreation;
        public double? CharacterAfterSeconds;
        public int Sent;
        public int FullSent;
        public string LastShort = "";
        public string LastText = "";
        public List<int> SecondsShown = new();
        public string Ended = "";
        public DateTime AtUtc;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Row> Rows = new();
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    internal static void Note(int userIndex, NoAnswerNoticeState s, string ended = "")
    {
        lock (Gate)
        {
            if (Rows.Count >= 256 && !Rows.ContainsKey(userIndex)) Rows.Clear();
            Rows[userIndex] = new Row
            {
                Name = s.Name, Generation = s.Generation, HeldForCreation = s.HeldForCreation,
                CharacterAfterSeconds = s.HeldForCreation && s.CountdownStart != null ? s.CountdownStart - s.AskedAt : null,
                Sent = s.NoticesSent, FullSent = s.FullSent, LastShort = s.LastShort, LastText = s.LastText, SecondsShown = new List<int>(s.SecondsShown), Ended = ended, AtUtc = Clock(),
            };
        }
    }

    internal static Dictionary<string, object?> Describe()
    {
        var users = new List<object?>();
        lock (Gate)
        {
            foreach (var pair in Rows)
            {
                var r = pair.Value;
                users.Add(new Dictionary<string, object?>
                {
                    ["user"] = pair.Key, ["name"] = r.Name, ["heldForCreation"] = r.HeldForCreation,
                    ["characterAfterSeconds"] = r.CharacterAfterSeconds, ["sent"] = r.Sent, ["fullSent"] = r.FullSent,
                    ["lastShort"] = r.LastShort, ["lastText"] = r.LastText,
                    ["secondsShown"] = new List<int>(r.SecondsShown), ["ended"] = r.Ended,
                    ["at"] = r.AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                });
            }
        }

        return new Dictionary<string, object?> { ["users"] = users };
    }

    internal static void Clear()
    {
        lock (Gate) Rows.Clear();
    }
}
