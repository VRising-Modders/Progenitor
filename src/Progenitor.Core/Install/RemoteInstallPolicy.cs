using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Install;

/// <summary>
/// REMOTEINST1: the server's <c>[RemoteInstall]</c> section as plain values (an immutable snapshot). Since the C4 live test
/// (2026-10-10) the server rebuilds it whenever the cfg's values differ from the snapshot's
/// (<see cref="RemoteInstallSettingsCache"/>), so a cfg edit applies on the next read without a Core reload.
/// </summary>
internal sealed class RemoteInstallSettings
{
    public const string Section = "RemoteInstall";
    public const string EnabledKey = "Enabled";
    public const string AllowedSteamIdsKey = "AllowedSteamIds";

    /// <summary>
    /// Settings organization stage 5, deprecated the same day for <c>[ServerSettings] LockedInGame</c>
    /// (<c>Networking.ServerSettingsLock</c>): false adds <c>progenitor/JoinAdmin/allowed-steam-ids</c> to the lock list.
    /// </summary>
    public const string AllowedSteamIdsInGameKey = "AllowedSteamIdsInGame";
    public const string ThunderstoreBaseUrlKey = "ThunderstoreBaseUrl";
    public const string ConnectTimeoutKey = "ConnectTimeoutSeconds";
    public const string StallTimeoutKey = "StallTimeoutSeconds";
    public const string TotalTimeoutKey = "TotalTimeoutSeconds";

    public const string DefaultBaseUrl = "https://thunderstore.io";
    public const int DefaultConnectTimeoutSeconds = 10;
    public const int DefaultStallTimeoutSeconds = 20;
    public const int DefaultTotalTimeoutSeconds = 120;

    /// <summary>The V Rising community path under the base URL; the same index the client reads.</summary>
    public const string CommunityIndexPath = "/c/v-rising/api/v1/package/";

    public static RemoteInstallSettings Default { get; } = new();

    /// <summary>
    /// What a server runs with until <c>[RemoteInstall]</c> is actually bound, and whenever reading it
    /// fails: switched off. A host who turned remote installs off, or narrowed them to listed admins,
    /// must never get the permissive default because the config file was slow to resolve.
    /// </summary>
    public static RemoteInstallSettings Unbound { get; } = new() { Enabled = false };

    public bool Enabled { get; init; } = true;

    /// <summary>Empty means any in-game admin.</summary>
    public IReadOnlySet<ulong> AllowedSteamIds { get; init; } = new HashSet<ulong>();

    public string ThunderstoreBaseUrl { get; init; } = DefaultBaseUrl;
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;
    public int StallTimeoutSeconds { get; init; } = DefaultStallTimeoutSeconds;
    public int TotalTimeoutSeconds { get; init; } = DefaultTotalTimeoutSeconds;

    /// <summary>True when <see cref="ThunderstoreBaseUrl"/> is an absolute https URL.</summary>
    public bool BaseUrlValid => TryBaseUri(out _);

    public bool TryBaseUri(out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate((ThunderstoreBaseUrl ?? "").Trim(), UriKind.Absolute, out var parsed) || parsed == null) return false;
        // LOCALTS1: plain http only for a loopback dev Thunderstore in a developer tree.
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !ThunderstoreMirror.AllowsHttpBase(parsed)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The package index this server reads: <c>&lt;base&gt;/c/v-rising/api/v1/package/</c>.</summary>
    public string IndexUrl => (ThunderstoreBaseUrl ?? DefaultBaseUrl).Trim().TrimEnd('/') + CommunityIndexPath;

    /// <summary>
    /// The host a package download must come from: the base URL's host or one of its subdomains
    /// (Thunderstore's own download links redirect to its CDN under the same domain).
    /// </summary>
    public bool IsThunderstoreUrl(string? url)
    {
        if (!TryBaseUri(out var baseUri)) return false;
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var parsed) || parsed == null) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // LOCALTS1: a loopback dev base serves its own downloads (same scheme, host and port).
            return string.Equals(parsed.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                && parsed.Port == baseUri.Port;
        }
        string host = parsed.Host;
        string root = baseUri.Host;
        return string.Equals(host, root, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
    }

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(Clamp(ConnectTimeoutSeconds, DefaultConnectTimeoutSeconds));
    public TimeSpan StallTimeout => TimeSpan.FromSeconds(Clamp(StallTimeoutSeconds, DefaultStallTimeoutSeconds));
    public TimeSpan TotalTimeout => TimeSpan.FromSeconds(Clamp(TotalTimeoutSeconds, DefaultTotalTimeoutSeconds));

    private static int Clamp(int value, int fallback) => value is > 0 and <= 3600 ? value : fallback;

    /// <summary>
    /// <c>AllowedSteamIds</c> as typed: SteamID64 values separated by commas, semicolons or
    /// whitespace. Anything that is not a plausible SteamID64 is returned in
    /// <paramref name="rejected"/> and ignored.
    /// </summary>
    public static IReadOnlySet<ulong> ParseSteamIds(string? text, out IReadOnlyList<string> rejected)
    {
        var ids = new HashSet<ulong>();
        var bad = new List<string>();
        foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            if (IsSteamId64(token, out ulong id)) ids.Add(id);
            else bad.Add(token);
        }

        rejected = bad;
        return ids;
    }

    /// <summary>A 17-digit individual-account SteamID64 (<c>7656119…</c>).</summary>
    public static bool IsSteamId64(string? text, out ulong id)
    {
        id = 0;
        string t = (text ?? "").Trim();
        if (t.Length != 17 || !t.StartsWith("7656119", StringComparison.Ordinal)) return false;
        return ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}

/// <summary>REMOTEINST1: a sliding-window limit per admin SteamID. Game-free; the caller supplies the clock.</summary>
internal sealed class RemoteInstallRateLimiter
{
    public const int DefaultMaxOperations = 5;
    public const double DefaultWindowSeconds = 60;

    private readonly int _max;
    private readonly double _window;
    private readonly Dictionary<ulong, Queue<double>> _taken = new();
    private readonly object _gate = new();

    public RemoteInstallRateLimiter(int maxOperations = DefaultMaxOperations, double windowSeconds = DefaultWindowSeconds)
    {
        _max = maxOperations > 0 ? maxOperations : DefaultMaxOperations;
        _window = windowSeconds > 0 ? windowSeconds : DefaultWindowSeconds;
    }

    /// <summary>Spend one operation for <paramref name="steamId"/> at <paramref name="now"/>. False when the window is full.</summary>
    public bool TryTake(ulong steamId, double now)
    {
        lock (_gate)
        {
            if (!_taken.TryGetValue(steamId, out var times))
            {
                if (_taken.Count >= 1024) _taken.Clear();
                _taken[steamId] = times = new Queue<double>();
            }

            while (times.Count > 0 && (now - times.Peek() >= _window || now < times.Peek())) times.Dequeue();
            if (times.Count >= _max) return false;
            times.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Who sent one request, as the server re-read it off the sender's user entity.</summary>
internal readonly struct RemoteInstallActor
{
    /// <param name="authorizedBy">
    /// Why this requester may install: on the server's admin list, or <c>User.IsAdmin</c>. When not
    /// given it is derived from <paramref name="isAdmin"/> alone.
    /// </param>
    public RemoteInstallActor(string characterName, ulong steamId, bool isAdmin, RemoteAuthorizedBy? authorizedBy = null)
    {
        CharacterName = characterName ?? "";
        SteamId = steamId;
        IsAdmin = isAdmin;
        AuthorizedBy = authorizedBy ?? (isAdmin ? RemoteAuthorizedBy.AdminAuth : RemoteAuthorizedBy.None);
    }

    public string CharacterName { get; }
    public ulong SteamId { get; }

    /// <summary><c>User.IsAdmin</c>: ran <c>adminauth</c> this session.</summary>
    public bool IsAdmin { get; }

    public RemoteAuthorizedBy AuthorizedBy { get; }

    /// <summary>On the server's admin list, or authenticated with adminauth.</summary>
    public bool Authorized => AuthorizedBy != RemoteAuthorizedBy.None;

    /// <summary>Their SteamID is in the admin list file (membership is checked first).</summary>
    public bool OnAdminList => AuthorizedBy == RemoteAuthorizedBy.AdminList;
}

/// <summary>
/// REMOTEINST1: the request checks that need no network, in the order the contract names them.
/// The checks that do need it (the package is on Thunderstore's V Rising index at that exact
/// version) run inside the claimed slot, so a refused or busy request never reaches the network.
/// </summary>
internal static class RemoteInstallPolicy
{
    /// <summary>Is this user allowed to install here at all (the capability the status packet advertises)?</summary>
    public static bool Allowed(RemoteInstallSettings settings, in RemoteInstallActor actor) =>
        settings.Enabled && actor.Authorized && (settings.AllowedSteamIds.Count == 0 || settings.AllowedSteamIds.Contains(actor.SteamId));

    /// <summary>
    /// The first refusal for an install, update or uninstall, or <see cref="RemoteInstallReason.None"/>.
    /// Order: admin, enabled, allow-list, package and version syntax, rate limit, busy. The rate limit
    /// is spent only by a request that passed everything before it.
    /// </summary>
    public static RemoteInstallReason Check(
        RemoteInstallRequest request,
        in RemoteInstallActor actor,
        RemoteInstallSettings settings,
        RemoteInstallRateLimiter limiter,
        bool busy,
        double now)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!actor.Authorized) return RemoteInstallReason.NotAdmin;
        if (!settings.Enabled) return RemoteInstallReason.Disabled;
        if (settings.AllowedSteamIds.Count > 0 && !settings.AllowedSteamIds.Contains(actor.SteamId))
            return RemoteInstallReason.NotAllowed;
        // SRVUNINST1: an uninstall may name a plugin GUID instead (RemotePluginRef); nothing else may.
        bool addressable = RemoteInstallWire.IsValidPackage(request.Package)
            || (request.Op == RemoteInstallOp.Uninstall && RemotePluginRef.TryGuid(request.Package, out _));
        if (!addressable) return RemoteInstallReason.BadPackage;
        bool needsVersion = request.Op is RemoteInstallOp.Install or RemoteInstallOp.Update;
        if (needsVersion && !RemoteInstallWire.IsValidVersion(request.Version)) return RemoteInstallReason.BadVersion;
        if (!needsVersion && request.Version.Length > 0 && !RemoteInstallWire.IsValidVersion(request.Version))
            return RemoteInstallReason.BadVersion;
        if (!limiter.TryTake(actor.SteamId, now)) return RemoteInstallReason.RateLimited;
        if (busy) return RemoteInstallReason.Busy;
        return RemoteInstallReason.None;
    }
}

/// <summary>The <c>[RemoteInstall]</c> values as the cfg holds them now (before parsing).</summary>
internal readonly record struct RemoteInstallRaw(bool Enabled, string AllowedSteamIds, string BaseUrl, int ConnectSeconds, int StallSeconds, int TotalSeconds);

/// <summary>
/// The server's settings snapshot, rebuilt only when the cfg's values changed (live 2026-10-10: a changed
/// <c>ThunderstoreBaseUrl</c> applied only after a Core reload, because the snapshot was built once at bind). Pure.
/// </summary>
internal sealed class RemoteInstallSettingsCache
{
    private readonly object _gate = new();
    private RemoteInstallRaw? _raw;
    private RemoteInstallSettings _settings = RemoteInstallSettings.Unbound;

    /// <summary>The snapshot for <paramref name="raw"/>; <paramref name="rebuilt"/> when it differs from the last one read.</summary>
    public RemoteInstallSettings Read(RemoteInstallRaw raw, out bool rebuilt, out IReadOnlyList<string> rejected)
    {
        lock (_gate)
        {
            rejected = Array.Empty<string>();
            if (_raw is { } last && last == raw)
            {
                rebuilt = false;
                return _settings;
            }

            var ids = RemoteInstallSettings.ParseSteamIds(raw.AllowedSteamIds, out rejected);
            _settings = new RemoteInstallSettings
            {
                Enabled = raw.Enabled,
                AllowedSteamIds = ids,
                ThunderstoreBaseUrl = raw.BaseUrl ?? RemoteInstallSettings.DefaultBaseUrl,
                ConnectTimeoutSeconds = raw.ConnectSeconds,
                StallTimeoutSeconds = raw.StallSeconds,
                TotalTimeoutSeconds = raw.TotalSeconds,
            };
            _raw = raw;
            rebuilt = true;
            return _settings;
        }
    }

    /// <summary>Back to unbound (a Core unload).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _raw = null;
            _settings = RemoteInstallSettings.Unbound;
        }
    }
}
