using System;
using System.Globalization;

namespace Progenitor.Install;

/// <summary>INSTALLERR1: why the mod list could not be loaded, in the player's terms.</summary>
internal enum IndexProblemCause
{
    /// <summary>Something went wrong that none of the others names.</summary>
    Unknown = 0,

    /// <summary>The address answered with "connection refused" (nothing listens there).</summary>
    Refused = 1,

    /// <summary>No connection at all: no network, the name did not resolve, a secure connection failed.</summary>
    Offline = 2,

    /// <summary>The site did not answer in time.</summary>
    Timeout = 3,

    /// <summary>The site answered with an error (rate limit, outage, refused request).</summary>
    ServerError = 4,

    /// <summary>An answer came back but it was not a usable list (damaged, cut short, empty, too large).</summary>
    BadResponse = 5,

    /// <summary>Automatic checks are paused after repeated failures (the backoff).</summary>
    Paused = 6,
}

/// <summary>
/// INSTALLERR1: one failed mod-list load as the Install tab shows it: a short title, one plain sentence by cause, and the
/// technical detail kept for the log and the tooltip (never the headline). Game-free.
/// </summary>
internal sealed class IndexProblem
{
    public const string TitleUnreachable = "Can't reach the mod list";
    public const string TitleUnreadable = "Can't read the mod list";
    public const string TitlePaused = "Mod list checks are paused";

    /// <summary>The source's name in sentences: the real site, or the local stand-in.</summary>
    public const string RealSourceName = "thunderstore.io";
    public const string DevSourceName = "the local test mod list";

    private IndexProblem(IndexProblemCause cause, string title, string line, string detail)
    {
        Cause = cause;
        Title = title;
        Line = line;
        Detail = detail;
    }

    public IndexProblemCause Cause { get; }
    public string Title { get; }
    public string Line { get; }

    /// <summary>The raw error (exception text, HTTP status): the log and the tooltip only.</summary>
    public string Detail { get; }

    /// <summary>The cause word for the log and the bridge readback.</summary>
    public string CauseWord => Word(Cause);

    internal static string Word(IndexProblemCause cause) => cause switch
    {
        IndexProblemCause.Refused => "refused",
        IndexProblemCause.Offline => "offline",
        IndexProblemCause.Timeout => "timeout",
        IndexProblemCause.ServerError => "server-error",
        IndexProblemCause.BadResponse => "bad-response",
        IndexProblemCause.Paused => "paused",
        _ => "unknown",
    };

    /// <summary>
    /// The problem for <paramref name="error"/> (the fetch or load result's error text). <paramref name="devSource"/> is
    /// the local stand-in's <c>host:port</c> while it is the source, else null.
    /// </summary>
    public static IndexProblem From(string? error, string? devSource)
    {
        string detail = (error ?? "").Trim();
        var cause = Classify(detail);
        return new IndexProblem(cause, TitleFor(cause), LineFor(cause, devSource), detail);
    }

    public static string TitleFor(IndexProblemCause cause) => cause switch
    {
        IndexProblemCause.BadResponse => TitleUnreadable,
        IndexProblemCause.Paused => TitlePaused,
        _ => TitleUnreachable,
    };

    /// <summary>The plain sentence under the title.</summary>
    public static string LineFor(IndexProblemCause cause, string? devSource)
    {
        bool dev = !string.IsNullOrWhiteSpace(devSource);
        string source = dev ? DevSourceName : RealSourceName;
        switch (cause)
        {
            case IndexProblemCause.Refused when dev:
                return "Nothing is answering at " + devSource!.Trim() + ". Start the local test mod list, or switch back to thunderstore.io.";
            case IndexProblemCause.Refused:
            case IndexProblemCause.Offline:
                return dev
                    ? "Couldn't connect to " + source + " at " + devSource!.Trim() + ". Check that it is running, then try again."
                    : "Couldn't connect to thunderstore.io. Check your internet connection, then try again.";
            case IndexProblemCause.Timeout:
                return Capital(source) + " took too long to answer. It may be busy; try again in a moment.";
            case IndexProblemCause.ServerError:
                return Capital(source) + " had a problem answering. This is usually brief; try again in a few minutes.";
            case IndexProblemCause.BadResponse:
                return "The list that came back was damaged or incomplete. Try again; if it keeps happening, the site may be having trouble.";
            case IndexProblemCause.Paused:
                return "Automatic checks are paused after several failed tries. Retry checks now.";
            default:
                return "Something went wrong while loading it. Try again in a moment.";
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>The cause behind an error text from <see cref="CatalogFetcher"/>, the conditional check or the catalog.</summary>
    public static IndexProblemCause Classify(string? error)
    {
        string e = error ?? "";
        if (e.Length == 0) return IndexProblemCause.Unknown;
        if (Has(e, "checks paused after")) return IndexProblemCause.Paused;
        if (Has(e, "actively refused") || Has(e, "connection refused") || Has(e, "ConnectionRefused"))
            return IndexProblemCause.Refused;
        if (e.StartsWith(InstallReasons.FetchTimeout, StringComparison.Ordinal) || Has(e, "timed out") || Has(e, "timeout"))
            return IndexProblemCause.Timeout;
        if (Has(e, InstallReasons.FetchServerError) || Has(e, InstallReasons.FetchRateLimited) || Has(e, InstallReasons.FetchTooManyRetries)
            || Has(e, InstallReasons.FetchHttpError) || Has(e, "(HTTP "))
            return IndexProblemCause.ServerError;
        if (Has(e, InstallReasons.MalformedJson) || Has(e, InstallReasons.IndexNotArray) || Has(e, InstallReasons.IndexTruncated)
            || Has(e, InstallReasons.IndexEmpty) || Has(e, InstallReasons.IndexTooLarge) || Has(e, InstallReasons.FetchSizeCeiling)
            || Has(e, InstallReasons.FetchTruncated) || Has(e, InstallReasons.FetchRedirectBound) || Has(e, InstallReasons.FetchRedirectInsecure)
            || Has(e, "HTTP 304 without") || Has(e, "unsupported Content-Encoding") || Has(e, "prematurely"))
            return IndexProblemCause.BadResponse;
        if (Has(e, "No connection could be made") || Has(e, "No such host") || Has(e, "Name or service not known")
            || Has(e, "network is unreachable") || Has(e, "unreachable") || e.Contains("SSL") || e.Contains("TLS") || Has(e, "certificate")
            || Has(e, "Unable to connect") || Has(e, "host is down") || Has(e, InstallReasons.FetchTransport))
            return IndexProblemCause.Offline;
        return IndexProblemCause.Unknown;
    }

    private static bool Has(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---- The source and age line over a list on show ------------------------------------------------------------

    /// <summary>The dev source's notice (the stand-in's <c>host:port</c>).</summary>
    public static string DevNotice(string devSource) => "Using a local test mod list (" + devSource.Trim() + ")";

    /// <summary>A list from the disk cache, not refreshed yet.</summary>
    public static string LastListFrom(string when) => "Last list from " + when;

    /// <summary>A list kept on show after a failed refresh.</summary>
    public static string StaleNotice(string when) => "Showing the last list from " + when + "; couldn't refresh";

    /// <summary>The same when the list's time is unknown.</summary>
    public const string StaleNoticeNoTime = "Showing the last list; couldn't refresh";

    /// <summary>
    /// When a list was fetched, in the player's words: <c>today at 3:05 PM</c>, <c>yesterday at 9:12 AM</c>,
    /// <c>Oct 12 at 3:05 PM</c>, or <c>Oct 12, 2025</c> in another year. <paramref name="zone"/> is the player's (local when null).
    /// </summary>
    public static string When(DateTime stampUtc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var us = CultureInfo.GetCultureInfo("en-US");
        DateTime at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(stampUtc.ToUniversalTime(), DateTimeKind.Utc), zone);
        DateTime now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc.ToUniversalTime(), DateTimeKind.Utc), zone);
        string time = at.ToString("h:mm tt", us);
        if (at.Date == now.Date) return "today at " + time;
        if (at.Date == now.Date.AddDays(-1)) return "yesterday at " + time;
        if (at.Year == now.Year) return at.ToString("MMM d", us) + " at " + time;
        return at.ToString("MMM d, yyyy", us);
    }

    /// <summary>Parse an index stamp (<see cref="CatalogFetcher.FormatStamp"/>); false when unreadable.</summary>
    public static bool TryParseStamp(string? stamp, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(stamp)) return false;
        if (!DateTime.TryParse(stamp!.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    /// <summary>The local stand-in's <c>host:port</c> while it is the source, else null (<see cref="ThunderstoreMirror"/>).</summary>
    public static string? DevSourceNow()
    {
        try
        {
            var b = ThunderstoreMirror.Base;
            return b == null ? null : b.Host + ":" + b.Port.ToString(CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    /// <summary>The effective index URL now (the stand-in's while it is active, else thunderstore.io's).</summary>
    public static string SourceUrlNow()
    {
        try { return CatalogFetchOptions.Default.IndexUrl; }
        catch { return CatalogFetcher.DefaultIndexUrl; }
    }
}

/// <summary>
/// INSTALLERR1: the player's Retry, paced. The automatic checks already back off (<see cref="ConditionalIndexFetch"/>); a
/// Retry goes out during that pause, so repeated presses are spaced: free after the first failure, then 2 s, doubling per
/// further failure up to 30 s. A success clears it. Game-free.
/// </summary>
internal sealed class IndexRetryGate
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    private int _failures;
    private DateTime _notBefore = DateTime.MinValue;

    public int Failures => _failures;

    /// <summary>True when a Retry may go out now; else how long to wait.</summary>
    public bool CanRetry(DateTime nowUtc, out TimeSpan wait)
    {
        wait = _notBefore > nowUtc ? _notBefore - nowUtc : TimeSpan.Zero;
        return wait <= TimeSpan.Zero;
    }

    public void NoteFailure(DateTime nowUtc)
    {
        _failures++;
        // The first failure leaves Retry free (the player has not retried yet); each further one spaces it.
        if (_failures < 2)
        {
            _notBefore = DateTime.MinValue;
            return;
        }

        int doublings = Math.Min(_failures - 2, 10);
        long ticks = First.Ticks << doublings;
        if (ticks <= 0 || ticks > Max.Ticks) ticks = Max.Ticks;
        _notBefore = nowUtc + TimeSpan.FromTicks(ticks);
    }

    public void NoteSuccess()
    {
        _failures = 0;
        _notBefore = DateTime.MinValue;
    }

    /// <summary>The line under the Retry button while it waits: <c>Try again in 4 seconds.</c></summary>
    public static string WaitLine(TimeSpan wait)
    {
        int s = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return "Try again in " + s.ToString(CultureInfo.InvariantCulture) + (s == 1 ? " second." : " seconds.");
    }
}
