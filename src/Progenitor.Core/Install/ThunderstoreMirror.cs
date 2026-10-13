using System;

namespace Progenitor.Install;

/// <summary>
/// LOCALTS1 (dev only, user instruction 2026-10-09): a local stand-in for Thunderstore that new versions can be published
/// to (<c>tools/local-thunderstore</c>). <c>[Install] DevThunderstoreUrl</c> (cfg only, empty by default) names its base
/// URL; while it is set <b>and</b> this is a developer tree (<c>[Reload] WatchCore = true</c>, written by
/// <c>tools/deploy.ps1</c>) or <c>[Install] AllowDevThunderstore = true</c>, the client's index, package downloads, icons
/// and README requests go to it instead of thunderstore.io. The URL must be a loopback address (<c>127.0.0.1</c>,
/// <c>localhost</c>, <c>[::1]</c>); it is the only place plain http is accepted, so the https-only rule for real use is
/// unchanged. Anything else (a non-loopback host, no dev signal) leaves the mirror off and is logged once.
/// The server's remote installs follow <c>[RemoteInstall] ThunderstoreBaseUrl</c>, which accepts the same loopback http
/// URL under the same dev rule (<see cref="AllowsHttpBase"/>).
/// </summary>
internal static class ThunderstoreMirror
{
    public const string Section = "Install";
    public const string UrlKey = "DevThunderstoreUrl";
    public const string AllowKey = "AllowDevThunderstore";
    public const string CommunityIndexPath = "/c/v-rising/api/v1/package/";

    public const string UrlDescription =
        "Developer testing only. The base URL of a local Thunderstore stand-in (tools/local-thunderstore), for example "
        + "http://127.0.0.1:27450. Used only on a loopback address and only in a developer tree ([Reload] WatchCore = true) "
        + "or with AllowDevThunderstore = true. Empty (the default) uses thunderstore.io.";

    public const string AllowDescription =
        "Developer testing only. Lets DevThunderstoreUrl take effect outside a developer tree. Off by default.";

    /// <summary>The configured URL (live: the shared cfg). Null: nothing configured.</summary>
    internal static Func<string?>? UrlProbe { get; set; }
#if !PROGENITOR_TESTS
        = LiveUrl;
#endif

    /// <summary>The dev signal (live: WatchCore or AllowDevThunderstore).</summary>
    internal static Func<bool>? DevProbe { get; set; }
#if !PROGENITOR_TESTS
        = LiveDev;

    private static readonly object Gate = new();
    private static BepInEx.Configuration.ConfigEntry<string>? _url;
    private static BepInEx.Configuration.ConfigEntry<bool>? _allow;
    private static bool _bindTried;
    private static bool? _watchCore;

    private static void BindOnce()
    {
        lock (Gate)
        {
            if (_bindTried) return;
            _bindTried = true;
        }

        try
        {
            var file = Progenitor.Config.SharedConfig.TryGetOrLog("[" + Section + "] " + UrlKey);
            if (file == null)
            {
                lock (Gate) _bindTried = false; // the shared file is not up yet: try again on the next read
                return;
            }

            var url = Progenitor.Config.ConfigHelper.Bind(file, Section, UrlKey, "", UrlDescription);
            var allow = Progenitor.Config.ConfigHelper.Bind(file, Section, AllowKey, false, AllowDescription);
            lock (Gate)
            {
                _url = url;
                _allow = allow;
            }
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("Install: binding [" + Section + "] " + UrlKey + " threw (" + e.Message + "); no dev Thunderstore."); }
            catch { /* */ }
        }
    }

    private static string? LiveUrl()
    {
        BindOnce();
        lock (Gate) return _url?.Value;
    }

    private static bool LiveDev()
    {
        BindOnce();
        bool allow;
        lock (Gate) allow = _allow?.Value ?? false;
        if (allow) return true;
        if (_watchCore == null)
        {
            try
            {
                _watchCore = Progenitor.Resilience.InteropArchive.TryReadFlagFromConfig(
                    BepInEx.Paths.BepInExRootPath, "Reload", "WatchCore", out bool watch) && watch;
            }
            catch { _watchCore = false; }
        }

        return _watchCore.Value;
    }
#endif

    private static string _lastNote = "";

    /// <summary>Tests: the configured URL and dev signal for this test flow only (null: the probes).</summary>
    internal static (string? Url, bool Dev)? TestOverride
    {
        get => TestOverrideLocal.Value;
        set => TestOverrideLocal.Value = value;
    }

    private static readonly System.Threading.AsyncLocal<(string? Url, bool Dev)?> TestOverrideLocal = new();

    /// <summary>Why the mirror is on or off (readback and the log).</summary>
    public static string Note
    {
        get
        {
            Resolve(out string note);
            return note;
        }
    }

    public static bool Active => Resolve(out _) != null;

    /// <summary>The mirror's base (no trailing slash), or null when it is off.</summary>
    public static Uri? Base => Resolve(out _);

    public static string IndexUrl => Base is { } b ? Trim(b) + CommunityIndexPath : CatalogFetcher.DefaultIndexUrl;

    public static string IconRoot => Base is { } b ? Trim(b) + "/icons/" : ThunderstoreAdapter.IconCdnRoot;

    public static string ExperimentalPackageRoot => Base is { } b ? Trim(b) + "/api/experimental/package/" : ThunderstoreAdapter.ExperimentalPackageRoot;

    /// <summary>A URL on the active mirror (same scheme, host and port).</summary>
    public static bool IsMirrorUrl(string? url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) && IsMirrorUri(u);

    public static bool IsMirrorUri(Uri? u)
    {
        var b = Base;
        if (b == null || u == null) return false;
        return string.Equals(u.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && u.Port == b.Port;
    }

    /// <summary>The fetcher's https rule lets this http URL through: it is on the active loopback mirror.</summary>
    public static bool AllowsHttp(Uri? u) =>
        u != null && string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && u.IsLoopback && IsMirrorUri(u);

    /// <summary>
    /// The server's <c>ThunderstoreBaseUrl</c> may be plain http when it is a loopback address and the dev signal is on.
    /// </summary>
    public static bool AllowsHttpBase(Uri? u)
    {
        if (u == null || !u.IsLoopback || !string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
        if (TestOverride is { } test) return test.Dev;
        try { return DevProbe?.Invoke() ?? false; }
        catch { return false; }
    }

    /// <summary>A loopback http(s) base URL, or null with the reason.</summary>
    public static Uri? ParseLoopback(string? text, out string why)
    {
        why = "";
        string t = (text ?? "").Trim();
        if (t.Length == 0) { why = "not set"; return null; }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u == null) { why = "not an absolute URL"; return null; }
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) { why = "not http or https"; return null; }
        if (!u.IsLoopback) { why = "not a loopback address (only 127.0.0.1, localhost or [::1] are accepted)"; return null; }
        return u;
    }

    private static Uri? Resolve(out string note)
    {
        var test = TestOverride;
        string? text;
        try { text = test != null ? test.Value.Url : UrlProbe?.Invoke(); }
        catch { text = null; }
        var u = ParseLoopback(text, out string why);
        if (u == null)
        {
            note = "off: [" + Section + "] " + UrlKey + " " + why;
            return null;
        }

        bool dev;
        try { dev = test != null ? test.Value.Dev : DevProbe?.Invoke() ?? false; }
        catch { dev = false; }
        if (!dev)
        {
            note = "off: " + u + " is set but this is not a developer tree ([Reload] WatchCore) and [" + Section + "] " + AllowKey + " is off";
            Log(note);
            return null;
        }

        note = "on: " + u;
        Log(note);
        return u;
    }

    private static void Log(string note)
    {
        if (string.Equals(note, _lastNote, StringComparison.Ordinal)) return;
        _lastNote = note;
#if !PROGENITOR_TESTS
        try { Plugin.Logger?.LogWarning("Install: dev Thunderstore " + note); }
        catch { /* */ }
#endif
    }

    private static string Trim(Uri b) => b.GetLeftPart(UriPartial.Path).TrimEnd('/');
}
