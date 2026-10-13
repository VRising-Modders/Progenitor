using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Progenitor.Support;

/// <summary>
/// SUPPORT1: removes personal details from every text file of a support bundle before it is zipped. One instance per
/// bundle, so the same SteamID, address or host gets the same placeholder in every file (<c>[steam-1]</c>, <c>[ip-1]</c>,
/// <c>[host-1]</c>), and a helper can still follow one player or one address through the logs. Game-free.
///
/// <para>What it replaces: SteamID64s; IPv4 addresses other than loopback, <c>0.x</c> and <c>255.x</c> (a dotted quad
/// right after <c>v</c>, <c>ver</c>, <c>version</c> or <c>build</c> is a version and stays); non-loopback IPv6 addresses;
/// host names in <c>steamipv4://</c> / <c>steamipv6://</c> connect strings; the Windows profile path; the user and
/// machine names (<see cref="NameRegex"/>); Discord webhook paths; the values of secret settings (<see cref="IsSecretKey"/>)
/// in cfg lines, <c>key=value</c> / <c>key: "value"</c> text, JSON, query strings, the game's "overridden by Command Line
/// Parameter" lines and <c>-password value</c> arguments; email addresses. Character names cannot be told apart from
/// other words, so they stay (README.txt says so).</para>
///
/// <para>Every pattern is linear in its input (a key is matched only from the start of its word, and every repeat is
/// bounded) and carries a match timeout; a text whose redaction still times out is withheld whole, never shipped
/// unredacted (<see cref="Withheld"/>).</para>
/// </summary>
internal sealed class SupportRedactor
{
    internal const string Hidden = "[hidden]";
    internal const string Email = "[email]";
    internal const string User = "[user]";
    internal const string Machine = "[machine]";
    internal const string Profile = "%USERPROFILE%";

    /// <summary>The longest one match may take before the text is withheld.</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static Regex Rx(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Opts | extra, MatchTimeout);

    // One word of a setting name: SessionToken, ServerHostSettings.Password, rcon-password. Matched only from the start of
    // its word (the guards below), and at most 128 characters, so no input makes the engine rescan a long run.
    private const string KeyChars = @"[A-Za-z0-9_.\-]";
    private const string Key = KeyChars + "{1,128}";

    private static readonly Regex Webhook = Rx(@"/api/webhooks/[^\s""'<>]+", RegexOptions.IgnoreCase);

    // cfg / ini: "SessionToken = abc def" → the whole rest of the line.
    // A quoted value is left to Assignment, which keeps the quotes.
    private static readonly Regex SecretLine = Rx(@"(?m)^([ \t]*(" + Key + @")[ \t]*=[ \t]*)([^\s""'][^\r\n]*)");

    // key=value, key: "value", "key": "value", key='value', ?token=value (the value up to a separator, or the quoted text).
    private static readonly Regex Assignment = Rx(
        @"(?<![A-Za-z0-9_.\-])([""']?)(" + Key + @")\1([ \t]*[=:][ \t]*)(?=\S)");

    // The game's own override line: ... - 'Password' (System.String) overridden by Command Line Parameter '-password' - New value: x
    private static readonly Regex Override = Rx(
        @"'(" + KeyChars + @"{1,64})'[^'\r\n]{0,300}?overridden by Command Line Parameter '(-{0,2}" + KeyChars + @"{1,64})'[^\r\n]{0,40}?New value: ([^\r\n]*)",
        RegexOptions.IgnoreCase);

    // A logged command line: -password hunter2, --rconPassword "a b", -token=x
    private static readonly Regex Argument = Rx(
        @"(?<![A-Za-z0-9_\-])(-{1,2}(" + KeyChars + @"{1,64}))([ \t]+|=)(?=\S)");

    private static readonly Regex EmailRx = Rx(
        @"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63}){0,8}\.[A-Za-z]{2,24}(?![A-Za-z0-9])");

    private static readonly Regex Steam = Rx(@"(?<!\d)7656119\d{10}(?!\d)");

    // The same account in its other spellings: the game's "{Steam 656922249}" (the 32-bit account id), SteamID3
    // "[U:1:656922249]" and the legacy "STEAM_0:1:328461124" (account id = 2 × the last number + the middle one).
    private static readonly Regex SteamBraced = Rx(@"\{Steam (\d{1,10})\}");
    private static readonly Regex Steam3 = Rx(@"\[U:1:(\d{1,10})\]");
    private static readonly Regex Steam2 = Rx(@"(?<![A-Za-z0-9_])STEAM_[0-5]:([01]):(\d{1,10})(?!\d)");

    /// <summary>A 32-bit Steam account id's SteamID64 (individual account, public universe).</summary>
    internal const ulong SteamId64Base = 0x0110000100000000UL; // universe 1, individual account, desktop instance

    /// <summary>The account id's SteamID64 as text, or null when it is not a 32-bit number.</summary>
    internal static string? SteamId64Of(string accountId)
    {
        if (!ulong.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong account) || account > uint.MaxValue) return null;
        return (SteamId64Base + account).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The placeholder for an account id: the SteamID64's own number when it maps to one, else a number of its own.</summary>
    private string AccountPlaceholder(string accountId, string original)
    {
        string? id64 = SteamId64Of(accountId);
        return Placeholder(_steam, id64 ?? original, "steam");
    }

    private static readonly Regex Ipv4 = Rx(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?!\d|\.\d)");

    private static readonly Regex VersionContext = Rx(@"(?i)(?:\bv|\bver|\bversion|\bbuild)[\s:=]*$");

    private static readonly Regex Ipv6 = Rx(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])");

    // The game's connect strings: steamipv4://play.example.com:9876 (an address there is already [ip-N]).
    private static readonly Regex ConnectHost = Rx(@"(?<![A-Za-z0-9])(steamipv4|steamipv6)://([A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63}){0,8})", RegexOptions.IgnoreCase);

    /// <summary>Last-word endings that make a setting or argument secret (compared lowercased, without <c>-</c> and <c>_</c>).</summary>
    private static readonly string[] SecretEndings =
    {
        "token", "password", "passwd", "pwd", "secret", "apikey", "webhookurl", "webhook", "privatekey", "accesskey",
        "secretkey", "authkey", "sessionkey", "licensekey", "credential", "credentials",
    };

    /// <summary>Whole last words that are secret on their own (<c>Key: "…"</c>, <c>-pass x</c>).</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal) { "key", "pass", "auth" };

    /// <summary>
    /// Account and computer names that are also ordinary words; never replaced, or the logs lose words like
    /// "server" and "admin" everywhere.
    /// </summary>
    private static readonly HashSet<string> CommonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "user", "users", "server", "steam", "owner", "player", "guest", "default", "public",
        "home", "desktop", "laptop", "windows", "game", "gaming", "vrising", "test", "local", "localhost", "root", "system",
        "client", "host", "pc", "computer", "workstation", "dev", "mod", "mods",
    };

    /// <summary>
    /// Words never treated as a player name even when one is spelled so: the placeholders' own words, log levels and
    /// the framework's names, so a character called <c>Error</c> or <c>Hidden</c> cannot rewrite every log line.
    /// </summary>
    private static readonly HashSet<string> NotPlayerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hidden", "email", "machine", "player", "players", "withheld", "truncated", "steam", "ip", "info", "warning", "error",
        "debug", "message", "fatal", "true", "false", "null", "none", "unknown", "progenitor", "bepinex", "unity", "server",
        "client", "admin", "administrator", "user", "users", "the", "and", "you", "all", "new", "vampire", "castle",
        // Common game words a character may be named after, which the logs also use as words.
        "ghost", "night", "blood", "test", "vampires", "castles", "servant", "servants", "ghoul", "wolf", "bat", "bear",
        "shadow", "moon", "sun", "dawn", "dusk", "day", "dark", "light", "death", "fire", "frost", "storm", "iron", "stone",
        "wood", "bone", "spell", "spells", "weapon", "armor", "boss", "lord", "king", "queen", "hunter", "slayer", "mist",
        "chaos", "unholy", "illusion", "default", "world", "map", "chat", "clan", "team", "guest", "bot", "npc",
    };

    private readonly HashSet<string> _excludedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Words that must never become <c>[player-N]</c> even when a player is named so: the loaded plugins' names, GUIDs,
    /// GUID parts and assembly names, so a character called like a mod cannot erase that mod's log tags.
    /// </summary>
    internal void ExcludeNames(IEnumerable<string>? words)
    {
        if (words == null) return;
        foreach (string w in words)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            string word = w.Trim();
            _excludedNames.Add(word);
            foreach (string part in word.Split('.', '-', '_', ' '))
                if (part.Length >= 3) _excludedNames.Add(part);
        }
        // A name added before the exclusions came in is dropped now.
        _wordNames.RemoveWhere(n => _excludedNames.Contains(n));
        _phraseNames.RemoveAll(n => _excludedNames.Contains(n));
        _phraseDirty = true;
        _minWordName = int.MaxValue;
        _maxWordName = 0;
        foreach (string n in _wordNames)
        {
            _minWordName = Math.Min(_minWordName, n.Length);
            _maxWordName = Math.Max(_maxWordName, n.Length);
        }
    }

    /// <summary>
    /// Progenitor's own stand-in for a player with no name yet (<c>Handshake.PlayerLabel</c>): <c>a new player</c>, with or
    /// without its <c> (…)</c> suffix. Only that label: real names such as "The Impaler" or "Bob (2)" are names.
    /// </summary>
    internal static bool IsPlaceholderLabel(string name)
    {
        const string Label = "a new player";
        string n = (name ?? "").Trim();
        if (!n.StartsWith(Label, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = n.Substring(Label.Length).Trim();
        return rest.Length == 0 || (rest.StartsWith("(", StringComparison.Ordinal) && rest.EndsWith(")", StringComparison.Ordinal));
    }

    /// <summary>The longest name learned or given (a Steam persona name is at most 32 characters; a character name less).</summary>
    internal const int MaxNameLength = 64;

    // The formats a player's name is written in, by the game and by Progenitor (every capture is one name):
    // the game's Steam line, Progenitor's audit logs (remote install, settings) and its server log lines.
    private static readonly Regex[] NameFormats =
    {
        Rx(@"PersonaName: ([^,\r\n]{1,64})"),
        Rx(@"\badmin=""([^""\r\n]{1,64})"""),
        Rx(@"\bsettings (?:accepted|refused) by ([^\r\n(]{1,64}?) \("),
        Rx(@"\bDebug build: (?:on|off|detached[^\r\n]{0,20}?) by ([^\r\n(]{1,64}?) \("),
        Rx(@"\bPhotoMode: ([^\r\n]{1,64}?) asked to (?:pause|resume)"),
        Rx(@"\breport(?: chunk| packet)? from ([^\r\n(]{1,64}?) \("),
        Rx(@"\bunit look\(s\) to ([^\r\n]{1,64}?)\.\s*$", RegexOptions.Multiline),
        Rx(@"\) to ([^\r\n:]{1,64}?): sent="),
    };

    private readonly Dictionary<string, string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wordNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _phraseNames = new();
    private Regex? _phraseRx;
    private bool _phraseDirty;
    private int _minWordName = int.MaxValue;
    private int _maxWordName;

    /// <summary>Distinct player names replaced so far in this bundle.</summary>
    internal int PlayerCount => _players.Count;

    /// <summary>Names this redactor replaces as <c>[player-N]</c>.</summary>
    internal int KnownNameCount => _wordNames.Count + _phraseNames.Count;

    /// <summary>
    /// A player's character or Steam name to replace everywhere as <c>[player-N]</c>, whole-word and case-insensitive.
    /// Ignored when shorter than 3 characters, longer than <see cref="MaxNameLength"/>, only digits, or a common word.
    /// </summary>
    internal void AddName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string n = name!.Trim();
        if (n.Length < 3 || n.Length > MaxNameLength) return;
        if (CommonNames.Contains(n) || NotPlayerNames.Contains(n) || _excludedNames.Contains(n) || IsPlaceholderLabel(n)) return;
        bool digits = true, word = true;
        foreach (char c in n)
        {
            if (!char.IsDigit(c)) digits = false;
            if (!IsNameChar(c)) word = false;
        }
        if (digits) return;
        if (word)
        {
            if (_wordNames.Add(n))
            {
                _minWordName = Math.Min(_minWordName, n.Length);
                _maxWordName = Math.Max(_maxWordName, n.Length);
            }
            return;
        }
        foreach (string p in _phraseNames) if (string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) return;
        _phraseNames.Add(n);
        _phraseDirty = true;
    }

    internal void AddNames(IEnumerable<string>? names)
    {
        if (names == null) return;
        foreach (string n in names) AddName(n);
    }

    /// <summary>
    /// The player names written in <paramref name="text"/> in a known format (<see cref="NameFormats"/>), added to
    /// <paramref name="into"/>. Linear; a format that times out is skipped.
    /// </summary>
    internal static void LearnNames(string? text, ICollection<string> into)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rx in NameFormats)
        {
            try
            {
                for (var m = rx.Match(text); m.Success; m = m.NextMatch())
                {
                    string name = m.Groups[1].Value.Trim();
                    if (name.Length >= 3 && !IsPlaceholderLabel(name) && !into.Contains(name)) into.Add(name);
                }
            }
            catch (RegexMatchTimeoutException) { /* that format only */ }
        }
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Every known name, whole-word: single-word names through one scan of the text's words against a set (linear in the
    /// text, whatever the number of names), names with spaces or symbols through one alternation regex.
    /// </summary>
    private string ReplacePlayers(string t)
    {
        if (_phraseNames.Count > 0)
        {
            if (_phraseDirty || _phraseRx == null)
            {
                var sorted = new List<string>(_phraseNames);
                sorted.Sort((a, b) => b.Length.CompareTo(a.Length));
                var parts = new List<string>(sorted.Count);
                foreach (string s in sorted) parts.Add(Regex.Escape(s));
                _phraseRx = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", parts) + @")(?![\p{L}\p{N}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
                _phraseDirty = false;
            }
            t = _phraseRx.Replace(t, m => Placeholder(_players, m.Value.ToLowerInvariant(), "player"));
        }

        if (_wordNames.Count == 0) return t;
        System.Text.StringBuilder? sb = null;
        int last = 0;
        int i = 0;
        while (i < t.Length)
        {
            if (!IsNameChar(t[i])) { i++; continue; }
            int start = i;
            while (i < t.Length && IsNameChar(t[i])) i++;
            int len = i - start;
            if (len < _minWordName || len > _maxWordName) continue;
            string word = t.Substring(start, len);
            if (!_wordNames.Contains(word)) continue;
            // A placeholder's own word ([steam-1], [player-2]) is never a name: NotPlayerNames keeps those out.
            sb ??= new System.Text.StringBuilder(t.Length);
            sb.Append(t, last, start - last).Append(Placeholder(_players, word.ToLowerInvariant(), "player"));
            last = i;
        }
        if (sb == null) return t;
        sb.Append(t, last, t.Length - last);
        return sb.ToString();
    }

    private readonly Dictionary<string, string> _steam = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ip = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _host = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _profile;
    private readonly Regex? _user;
    private readonly Regex? _machine;

    /// <param name="userProfile">The Windows profile folder (the folder under <c>Users</c> named for the account), replaced by <c>%USERPROFILE%</c>.</param>
    /// <param name="userName">The account name, replaced by <c>[user]</c> by the rule of <see cref="NameRegex"/>.</param>
    /// <param name="machineName">The computer name, replaced by <c>[machine]</c> by the same rule.</param>
    internal SupportRedactor(string? userProfile, string? userName, string? machineName)
    {
        _profile = string.IsNullOrWhiteSpace(userProfile) ? null : userProfile!.TrimEnd('\\', '/');
        if (_profile != null && _profile.Length < 4) _profile = null;
        _user = NameRegex(userName);
        _machine = NameRegex(machineName);
    }

    /// <summary>A redactor for this PC's profile, user and machine names, for status lines and error text shown to people.</summary>
    internal static SupportRedactor ForThisMachine()
    {
        string? profile = null, user = null, machine = null;
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { /* none */ }
        try { user = Environment.UserName; } catch { /* none */ }
        try { machine = Environment.MachineName; } catch { /* none */ }
        return new SupportRedactor(profile, user, machine);
    }

    /// <summary>One status or error line, redacted for this PC (<see cref="ForThisMachine"/>).</summary>
    internal static string Scrub(string? text) => ForThisMachine().Redact(text);

    /// <summary>Distinct SteamIDs replaced so far in this bundle.</summary>
    internal int SteamIdCount => _steam.Count;

    /// <summary>Distinct IP addresses replaced so far in this bundle.</summary>
    internal int AddressCount => _ip.Count;

    /// <summary>Distinct host names replaced so far in this bundle.</summary>
    internal int HostCount => _host.Count;

    /// <summary>Texts withheld because a pattern timed out.</summary>
    internal int WithheldCount { get; private set; }

    /// <summary>
    /// The account or computer name's pattern, or null. A name in <see cref="CommonNames"/> or shorter than 3 characters is
    /// never replaced. A name of 5 or more characters is replaced as a whole word anywhere; a 3- or 4-character name only
    /// as a path segment (right after <c>\</c> or <c>/</c>), where it cannot be an ordinary word of a sentence.
    /// </summary>
    internal static Regex? NameRegex(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        string w = word!.Trim();
        if (w.Length < 3 || CommonNames.Contains(w)) return null;
        string escaped = Regex.Escape(w);
        string pattern = w.Length >= 5
            ? @"(?<![A-Za-z0-9])" + escaped + @"(?![A-Za-z0-9])"
            : @"(?<=[\\/])" + escaped + @"(?![A-Za-z0-9])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>
    /// True for a setting or argument name whose value is secret: its last dotted word, lowercased without <c>-</c> and
    /// <c>_</c>, ends with token, password, secret, an API/private/access key, a webhook URL or a credential, or is
    /// <c>key</c>, <c>pass</c> or <c>auth</c>. <c>TokenFile</c> and <c>ToggleKeyHint</c> are not secret.
    /// </summary>
    internal static bool IsSecretKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name!.Trim().Trim('"', '\'').TrimStart('-');
        int dot = n.LastIndexOf('.');
        if (dot >= 0) n = n.Substring(dot + 1);
        n = n.Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (n.Length == 0) return false;
        if (SecretWords.Contains(n)) return true;
        foreach (string end in SecretEndings)
            if (n.EndsWith(end, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>What replaces a whole text whose redaction timed out.</summary>
    internal static string Withheld(int length) =>
        "[withheld: redaction timed out on this text; " + length.ToString(CultureInfo.InvariantCulture) + " characters left out]";

    /// <summary>The text with every detail above replaced. Null reads as empty. Never returns unredacted text.</summary>
    internal string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            return RedactCore(text!);
        }
        catch (RegexMatchTimeoutException)
        {
            WithheldCount++;
            return Withheld(text!.Length);
        }
    }

    /// <summary>Tests: an extra step run first (it may throw <see cref="RegexMatchTimeoutException"/>).</summary>
    internal Func<string, string>? StepForTests { get; set; }

    private string RedactCore(string text)
    {
        string t = StepForTests?.Invoke(text) ?? text;
        t = Webhook.Replace(t, "/api/webhooks/" + Hidden);
        t = Override.Replace(t, m => (IsSecretKey(m.Groups[1].Value) || IsSecretKey(m.Groups[2].Value)) && m.Groups[3].Length > 0
            ? m.Value.Substring(0, m.Groups[3].Index - m.Index) + Hidden
            : m.Value);
        t = SecretLine.Replace(t, m => IsSecretKey(m.Groups[2].Value) ? m.Groups[1].Value + Hidden : m.Value);
        t = ReplaceValues(Assignment, t, argument: false);
        t = ReplaceValues(Argument, t, argument: true);
        t = EmailRx.Replace(t, Email);
        t = Steam.Replace(t, m => Placeholder(_steam, m.Value, "steam"));
        t = SteamBraced.Replace(t, m => "{Steam " + AccountPlaceholder(m.Groups[1].Value, m.Value) + "}");
        t = Steam3.Replace(t, m => AccountPlaceholder(m.Groups[1].Value, m.Value));
        t = Steam2.Replace(t, m =>
        {
            ulong.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong z);
            ulong account = z * 2 + (m.Groups[1].Value == "1" ? 1UL : 0UL);
            return AccountPlaceholder(account.ToString(CultureInfo.InvariantCulture), m.Value);
        });
        string beforeIp = t;
        t = Ipv4.Replace(beforeIp, m => ReplaceIpv4(m, beforeIp));
        t = Ipv6.Replace(t, ReplaceIpv6);
        t = ConnectHost.Replace(t, ReplaceConnectHost);

        if (_profile != null)
        {
            t = ReplaceIgnoreCase(t, _profile, Profile);
            t = ReplaceIgnoreCase(t, _profile.Replace('\\', '/'), Profile);
            t = ReplaceIgnoreCase(t, _profile.Replace("\\", "\\\\"), Profile); // JSON-escaped
        }
        t = ReplacePlayers(t);
        if (_user != null) t = _user.Replace(t, User);
        if (_machine != null) t = _machine.Replace(t, Machine);
        return t;
    }

    /// <summary>
    /// <see cref="Assignment"/> and <see cref="Argument"/> match only the name and separator, so a name that is not secret
    /// consumes nothing after its separator and the next name (<c>host_capture: Settings.Password="x"</c>,
    /// <c>-flag -password x</c>) is still matched. Only for a secret name is the value measured, by a plain scan
    /// (<see cref="ValueEnd"/>): no regex ever rescans a value. The value is replaced by <c>[hidden]</c>; quotes are kept and
    /// an empty quoted value stays empty (nothing to hide, and "no password" is useful to a helper).
    /// </summary>
    private static string ReplaceValues(Regex rx, string text, bool argument)
    {
        System.Text.StringBuilder? sb = null;
        int last = 0;
        for (var m = rx.Match(text); m.Success; m = m.NextMatch())
        {
            if (m.Index < last) continue; // inside a value already replaced
            if (!IsSecretKey(m.Groups[2].Value)) continue;
            int start = m.Index + m.Length;
            if (start >= text.Length) continue;
            char first = text[start];
            if (argument && first == '-') continue; // the next flag, not a value
            if (string.CompareOrdinal(text, start, "[hidden", 0, 7) == 0) continue;
            int end = ValueEnd(text, start, argument, out bool quoted);
            string with;
            if (quoted)
            {
                if (end - start <= 2) continue; // "" or ''
                with = first + Hidden + text[end - 1];
            }
            else
            {
                if (end <= start) continue;
                with = Hidden;
            }
            sb ??= new System.Text.StringBuilder(text.Length);
            sb.Append(text, last, start - last).Append(with);
            last = end;
        }
        if (sb == null) return text;
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// Where the value starting at <paramref name="start"/> ends (exclusive). A quoted value runs to its closing quote
    /// (a <c>\"</c> inside double quotes is escaped) or the end of the line; an unquoted one to whitespace, a quote or, in
    /// <c>key=value</c> text, one of <c>&amp; ; , &lt; &gt; ] )</c>.
    /// </summary>
    private static int ValueEnd(string text, int start, bool argument, out bool quoted)
    {
        char first = text[start];
        quoted = first == '"' || (!argument && first == '\'');
        int i = start + 1;
        if (quoted)
        {
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n') { quoted = false; return i; } // unterminated: hide to the end of the line
                if (c == '\\' && first == '"' && i + 1 < text.Length && text[i + 1] != '\r' && text[i + 1] != '\n') { i++; continue; }
                if (c == first) return i + 1;
            }
            quoted = false;
            return i;
        }
        for (i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '"' || c == '\'') break;
            if (!argument && (c == '&' || c == ';' || c == ',' || c == '<' || c == '>' || c == ']' || c == ')')) break;
        }
        return i;
    }

    private string ReplaceConnectHost(Match m)
    {
        string host = m.Groups[2].Value;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return m.Value;
        // A dotted quad left here was kept on purpose (loopback): only names are hosts.
        bool numeric = true;
        foreach (char c in host) if (!(char.IsDigit(c) || c == '.')) { numeric = false; break; }
        if (numeric) return m.Value;
        return m.Groups[1].Value + "://" + Placeholder(_host, host, "host");
    }

    private string ReplaceIpv4(Match m, string input)
    {
        var octets = new int[4];
        for (int i = 0; i < 4; i++)
        {
            string g = m.Groups[i + 1].Value;
            if (!int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]) || octets[i] > 255) return m.Value;
        }
        // Loopback, "this network" and broadcast/netmask values identify nobody.
        if (octets[0] == 127 || octets[0] == 0 || octets[0] == 255) return m.Value;

        // A version written like an address: "v1.2.3.4", "Version=1.0.0.0", "build 1.0.2.3".
        int from = Math.Max(0, m.Index - 12);
        string before = input.Substring(from, m.Index - from);
        if (VersionContext.IsMatch(before)) return m.Value;

        return Placeholder(_ip, m.Value, "ip");
    }

    private string ReplaceIpv6(Match m)
    {
        string v = m.Value;
        int doubles = CountOf(v, "::");
        if (doubles > 1) return v;
        string[] groups = v.Split(':');
        int nonEmpty = 0;
        bool hasLetter = false;
        foreach (string g in groups)
        {
            if (g.Length == 0) continue;
            nonEmpty++;
            foreach (char c in g) if (char.IsLetter(c)) hasLetter = true;
        }

        if (doubles == 0)
        {
            if (groups.Length != 8 || nonEmpty != 8) return v; // a full address has eight groups
        }
        else
        {
            // "::", "::1" (loopback) and two-part shapes like "12::34" stay.
            if (nonEmpty < 2) return v;
            if (!hasLetter && nonEmpty < 3) return v;
        }
        return Placeholder(_ip, v, "ip");
    }

    private static int CountOf(string text, string part)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }

    private static string Placeholder(Dictionary<string, string> map, string value, string kind)
    {
        if (!map.TryGetValue(value, out var name))
        {
            name = "[" + kind + "-" + (map.Count + 1).ToString(CultureInfo.InvariantCulture) + "]";
            map[value] = name;
        }
        return name;
    }

    private static string ReplaceIgnoreCase(string text, string find, string with)
    {
        if (find.Length == 0) return text;
        int at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int last = 0;
        while (at >= 0)
        {
            sb.Append(text, last, at - last).Append(with);
            last = at + find.Length;
            at = text.IndexOf(find, last, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }
}
