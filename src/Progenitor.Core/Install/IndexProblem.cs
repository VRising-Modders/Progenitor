using System;
using System.Globalization;

namespace Progenitor.Install;

/// <summary>INSTALLERR1: why the mod list could not be loaded, in the player's terms (the log and the tooltip).</summary>
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

    /// <summary>Automatic checks are paused after repeated failures (the closed tab's backoff).</summary>
    Paused = 6,
}

/// <summary>
/// INSTALLERR1: one failed mod-list check. The Install tab retries by itself and says little; this carries the cause (from
/// the transport's classification of the exception, never its localized text) and the sentence for the tooltip and the
/// log, with the raw error kept as <see cref="Detail"/>. Game-free.
/// </summary>
internal sealed class IndexProblem
{
    public const string RealSourceName = "thunderstore.io";
    public const string DevSourceName = "the local test mod list";

    private IndexProblem(IndexProblemCause cause, string line, string detail)
    {
        Cause = cause;
        Line = line;
        Detail = detail;
    }

    public IndexProblemCause Cause { get; }

    /// <summary>The plain sentence for the tooltip.</summary>
    public string Line { get; }

    /// <summary>The raw error (exception text, HTTP status): the log and the tooltip only.</summary>
    public string Detail { get; }

    /// <summary>The cause word for the log and the bridge readback.</summary>
    public string CauseWord => Word(Cause);

    /// <summary>The tooltip text: the sentence, then the raw error.</summary>
    public string Tooltip => Detail.Length == 0 ? Line : Line + "\n\n" + Detail;

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
    /// The problem for a failed check. The cause comes from the transport's classification of the exception
    /// (<paramref name="transport"/>, <see cref="RecordingCatalogTransport.Classify"/>: socket error codes and exception
    /// types, so a localized OS message changes nothing), the HTTP status and the backoff flag; only when none of them says
    /// anything is the error text read, and then only for this pipeline's own sentences (<see cref="InstallReasons"/>).
    /// </summary>
    public static IndexProblem From(FetchFailureCause transport, int status, bool paused, string? error, string? devSource)
    {
        string detail = (error ?? "").Trim();
        var cause = Classify(transport, status, paused, detail);
        return new IndexProblem(cause, LineFor(cause, devSource), detail);
    }

    /// <summary>The problem for a failed <see cref="CatalogLoadResult"/>.</summary>
    public static IndexProblem From(CatalogLoadResult result, string? devSource) =>
        From(result.FailureCause, result.StatusCode, result.Paused, result.Error, devSource);

    /// <summary>The tooltip's sentence by cause. No buttons are named: the tab keeps trying by itself.</summary>
    public static string LineFor(IndexProblemCause cause, string? devSource)
    {
        bool dev = !string.IsNullOrWhiteSpace(devSource);
        string source = dev ? DevSourceName : RealSourceName;
        switch (cause)
        {
            case IndexProblemCause.Refused when dev:
                return "Nothing is answering at " + devSource!.Trim() + ".";
            case IndexProblemCause.Refused:
            case IndexProblemCause.Offline:
                return dev
                    ? "Couldn't connect to " + source + " at " + devSource!.Trim() + "."
                    : "Couldn't connect to thunderstore.io. Check your internet connection; the list loads by itself once it can.";
            case IndexProblemCause.Timeout:
                return Capital(source) + " is taking too long to answer.";
            case IndexProblemCause.ServerError:
                return Capital(source) + " had a problem answering. This is usually brief.";
            case IndexProblemCause.BadResponse:
                return "The list that came back was damaged or incomplete.";
            case IndexProblemCause.Paused:
                return "Checks are paused for a few minutes after several failed tries.";
            default:
                return "Something went wrong while loading the list.";
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>The player's cause for a transport cause, an HTTP status and the backoff flag.</summary>
    public static IndexProblemCause Classify(FetchFailureCause transport, int status, bool paused, string? error)
    {
        if (paused) return IndexProblemCause.Paused;
        switch (transport)
        {
            case FetchFailureCause.Refused: return IndexProblemCause.Refused;
            case FetchFailureCause.Dns:
            case FetchFailureCause.Network:
            case FetchFailureCause.Tls: return IndexProblemCause.Offline;
            case FetchFailureCause.Timeout:
            case FetchFailureCause.Stalled: return IndexProblemCause.Timeout;
            case FetchFailureCause.Http: return IndexProblemCause.ServerError;
            case FetchFailureCause.Truncated: return IndexProblemCause.BadResponse;
        }

        if (status >= 400) return IndexProblemCause.ServerError;
        return Classify(error);
    }

    /// <summary>
    /// The cause behind one of this pipeline's own error sentences (<see cref="InstallReasons"/>, the backoff's skip line).
    /// OS and exception messages are localized and are never matched: they are only detail.
    /// </summary>
    public static IndexProblemCause Classify(string? error)
    {
        string e = error ?? "";
        if (e.Length == 0) return IndexProblemCause.Unknown;
        if (Has(e, "checks paused after")) return IndexProblemCause.Paused;
        if (e.StartsWith(InstallReasons.FetchTimeout, StringComparison.Ordinal)) return IndexProblemCause.Timeout;
        if (Has(e, InstallReasons.FetchServerError) || Has(e, InstallReasons.FetchRateLimited) || Has(e, InstallReasons.FetchTooManyRetries)
            || Has(e, InstallReasons.FetchHttpError))
            return IndexProblemCause.ServerError;
        if (Has(e, InstallReasons.MalformedJson) || Has(e, InstallReasons.IndexNotArray) || Has(e, InstallReasons.IndexTruncated)
            || Has(e, InstallReasons.IndexEmpty) || Has(e, InstallReasons.IndexTooLarge) || Has(e, InstallReasons.FetchSizeCeiling)
            || Has(e, InstallReasons.FetchTruncated) || Has(e, InstallReasons.FetchRedirectBound) || Has(e, InstallReasons.FetchRedirectInsecure)
            || Has(e, "HTTP 304 without") || Has(e, "unsupported Content-Encoding"))
            return IndexProblemCause.BadResponse;
        // A transport failure whose exception type said nothing more: no connection, in the player's terms.
        if (e.StartsWith(InstallReasons.FetchTransport, StringComparison.Ordinal)) return IndexProblemCause.Offline;
        return IndexProblemCause.Unknown;
    }

    private static bool Has(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---- The tab's few words --------------------------------------------------------------------------------------

    /// <summary>No list yet, a check running or due.</summary>
    public static string Connecting(bool dev) => dev ? "Connecting to the local test mod list…" : "Connecting to Thunderstore…";

    /// <summary>No list yet, after <see cref="IndexAutoRetry.QuietFailures"/> failed checks in a row.</summary>
    public static string StillTrying(bool dev) =>
        dev ? "Can't reach the local test mod list right now; still trying" : "Can't reach Thunderstore right now; still trying";

    /// <summary>The local stand-in is the source.</summary>
    public static string DevNotice(string devSource) => "Using a local test mod list (" + devSource.Trim() + ")";

    /// <summary>The local stand-in refused, and this session uses thunderstore.io instead (the cfg is untouched).</summary>
    public const string DevFallbackNotice = "Local test mod list unavailable; using Thunderstore";

    /// <summary>
    /// The list's age in the player's words: <c>Updated just now</c>, <c>Updated 5 minutes ago</c>, <c>Updated 2 hours ago</c>,
    /// <c>Updated yesterday</c>, <c>Updated 3 days ago</c>.
    /// </summary>
    public static string UpdatedAgo(DateTime stampUtc, DateTime nowUtc)
    {
        var age = nowUtc.ToUniversalTime() - stampUtc.ToUniversalTime();
        if (age < TimeSpan.FromMinutes(1)) return "Updated just now";
        if (age < TimeSpan.FromHours(1)) return "Updated " + Count((int)age.TotalMinutes, "minute") + " ago";
        if (age < TimeSpan.FromHours(24)) return "Updated " + Count((int)age.TotalHours, "hour") + " ago";
        if (age < TimeSpan.FromHours(48)) return "Updated yesterday";
        return "Updated " + Count((int)age.TotalDays, "day") + " ago";
    }

    private static string Count(int n, string unit) => n.ToString(CultureInfo.InvariantCulture) + " " + unit + (n == 1 ? "" : "s");

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

/// <summary>INSTALLERR1: what the Mods menu does about the index source on a check.</summary>
internal enum IndexSourceDecision
{
    /// <summary>The catalog is built for the current source.</summary>
    Same = 0,

    /// <summary>The source changed, but an update, a download, a review or a write is running: switch when it ends.</summary>
    Defer = 1,

    /// <summary>Rebuild the catalog for the new source now.</summary>
    Rebuild = 2,
}

/// <summary>INSTALLERR1: the source-change decision (<c>ModsMenu.FollowIndexSource</c>). Game-free.</summary>
internal static class IndexSourceFollow
{
    public static IndexSourceDecision Decide(string? want, string? builtFor, bool updatesBusy, bool installFlowActive)
    {
        if (string.Equals((want ?? "").Trim(), (builtFor ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return IndexSourceDecision.Same;
        return updatesBusy || installFlowActive ? IndexSourceDecision.Defer : IndexSourceDecision.Rebuild;
    }
}

/// <summary>
/// INSTALLERR1 (user decision 2026-10-14: "retry automatically, don't worry about the buttons"): while the Install tab is
/// shown, a failed check is retried by itself after 2 s, 5 s, 10 s and 30 s, then every 60 s; a success ends it. The
/// closed tab keeps the conditional check's own backoff (<see cref="ConditionalIndexFetch"/>). One failure per check.
/// Game-free.
/// </summary>
internal sealed class IndexAutoRetry
{
    /// <summary>The delays after the 1st, 2nd, 3rd and 4th failure in a row; every later one waits <see cref="Steady"/>.</summary>
    public static readonly TimeSpan[] Ladder =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    };

    public static readonly TimeSpan Steady = TimeSpan.FromSeconds(60);

    /// <summary>Failed checks in a row before the tab says it can't reach the list (until then it reads Connecting).</summary>
    public const int QuietFailures = 3;

    private int _failures;

    public int Failures => _failures;

    /// <summary>The tab has failed often enough to say so.</summary>
    public bool Loud => _failures >= QuietFailures;

    /// <summary>Note one failed check; the delay before the next automatic one.</summary>
    public TimeSpan NoteFailure()
    {
        _failures++;
        return DelayAfter(_failures);
    }

    public void NoteSuccess() => _failures = 0;

    public static TimeSpan DelayAfter(int failures) =>
        failures <= 0 ? TimeSpan.Zero : failures <= Ladder.Length ? Ladder[failures - 1] : Steady;
}
