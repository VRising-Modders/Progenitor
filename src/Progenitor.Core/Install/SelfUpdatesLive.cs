#if !PROGENITOR_TESTS
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE5: the live half of <see cref="SelfUpdates"/>: the probes (Bootstrap by reflection, the cached index, the
/// settings, the apply window) and a once-a-second tick on <see cref="Plugin.Frame"/>. Installed and removed with Core.
/// </summary>
internal static class SelfUpdatesLive
{
    private static readonly object Owner = new();
    private static Action? _frame;
    private static double _next;
    private static ConfigEntry<string>? _mode;
    private static ConfigEntry<bool>? _devOverride;
    private static ConfigEntry<string>? _devKey;
    private static bool _bindTried;
    private static byte[]? _shaBytes;
    private static string _coreSha = "";

    public static void Install()
    {
        try
        {
            Wire();
            _frame = OnFrame;
            Plugin.Frame += _frame;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning(SelfUpdates.LogPrefix + "could not start: " + e.Message); }
    }

    public static void Uninstall()
    {
        try { if (_frame != null) Plugin.Frame -= _frame; }
        catch { /* tearing down */ }
        _frame = null;
        _mode = null;
        _devOverride = null;
        _devKey = null;
        _bindTried = false;
        _shaBytes = null;
        _check = null;
        _checkUrl = "";
        SelfUpdates.Reset();
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static void OnFrame()
    {
        double now = Now();
        if (now < _next) return;
        _next = now + 1.0;
        try { SelfUpdates.Tick(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(SelfUpdates.LogPrefix + "tick threw: " + e.Message);
            _next = now + 10;
        }
    }

    private static void Wire()
    {
        SelfUpdates.RootProbe = () => Paths.BepInExRootPath;
        SelfUpdates.IsServerProbe = () => Plugin.IsServerProcess;
        SelfUpdates.FactsProbe = ReadFacts;
        // Both sides read Progenitor's signed release list (GitHub Pages; jsDelivr when that fails) into its own cache; a
        // client asks when it reads the mod index anyway, a server on its CheckHours cadence (SelfUpdates.Feed.cs).
        SelfUpdates.IndexPathProbe = () => FeedCache().IndexPath;
        SelfUpdates.KeysProbe = Keys;
        SelfUpdates.FeedStampProbe = () => FeedCache().TryReadStamp(out var stamp) ? stamp : null;
        SelfUpdates.IndexReadProbe = Plugin.IsServerProcess ? null : ModIndexStamp;
        SelfUpdates.CheckHoursProbe = () => Plugin.IsServerProcess ? ServerAutoUpdateLive.ReadSettings().CheckHours : ServerUpdateSettings.DefaultCheckHours;
        SelfUpdates.Download = DownloadAsync;
        SelfUpdates.CheckIndex = CheckFeedAsync;
        SelfUpdates.RequestSwap = RequestSwap;
        SelfUpdates.ReloadStatus = ReadReloadStatus;
        SelfUpdates.WindowClosed = Plugin.IsServerProcess ? ServerWindow : ClientWindow;
        SelfUpdates.WorldReady = () => !Plugin.IsServerProcess || (Worlds.Server is { IsCreated: true });
        SelfUpdates.Allowed = (name, sha) => Reload.ReloadAllowPolicy.MayLoad(Reload.ReloadAllowStore.Decide(name, sha));
        SelfUpdates.BootstrapPathProbe = BootstrapPath;
        SelfUpdates.RunningCoreBytes = CoreBytes;
        SelfUpdates.ProtocolProbe = () => BootstrapLink.ProtocolVersion;
        SelfUpdates.PostMain = a => Progenitor.Dev.MainThread.Post(a, Owner);
        SelfUpdates.Log = line => Plugin.Logger?.LogInfo(line);
        // Mod compatibility C4: while connected, the joined server's Progenitor requirement (its verified catalog) refuses a
        // click on Update for another build.
        SelfUpdates.ConnectedRequirement = () =>
        {
            if (Plugin.IsServerProcess) return null;
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            return Progenitor.Networking.ProgenitorExact.RequirementOf(Progenitor.Networking.ModManifest.LastEnvelope?.Body);
        };
        // C4: the join screens redraw their Progenitor row when the releases of the index were read again.
        SelfUpdates.ReleasesRead = Progenitor.Networking.JoinFlowClient.MarkVersionsDirty;
        SelfUpdates.Notice = (updated, failed, detail) =>
        {
            if (Plugin.IsServerProcess) return;
            ClientAutoUpdate.SetNotice(new UpdateNotice { Updated = updated, Failed = failed, Detail = detail, Utc = DateTime.UtcNow });
        };
    }

    // ---- settings -------------------------------------------------------------------------------------------

    private static void Bind()
    {
        if (_bindTried && _mode != null) return;
        _bindTried = true;
        try
        {
            var file = Progenitor.Config.SharedConfig.TryGetOrLog("[" + SelfUpdateSettings.Section + "] " + SelfUpdateSettings.ProgenitorKey);
            if (file == null)
            {
                _bindTried = false;
                return;
            }

            _mode = Progenitor.Config.ConfigHelper.Bind(file, SelfUpdateSettings.Section, SelfUpdateSettings.ProgenitorKey,
                SelfUpdateSettings.DefaultFor(Plugin.IsServerProcess),
                Plugin.IsServerProcess ? SelfUpdateSettings.ServerHelp : "Automatic, Notify or Off. " + SelfUpdateSettings.Help);
            _devOverride = Progenitor.Config.ConfigHelper.Bind(file, SelfUpdateSettings.Section, SelfUpdateSettings.DevOverrideKey, false,
                SelfUpdateSettings.DevOverrideHelp);
            _devKey = Progenitor.Config.ConfigHelper.Bind(file, SelfUpdateSettings.Section, SelfUpdateSettings.DevKeyKey, "",
                SelfUpdateSettings.DevKeyHelp);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning(SelfUpdates.LogPrefix + "binding [AutoUpdate] Progenitor threw: " + e.Message); }
    }

    // ---- facts ----------------------------------------------------------------------------------------------

    private static SelfFacts ReadFacts()
    {
        Bind();
        string root = Paths.BepInExRootPath;
        string bootPath = BootstrapPath() ?? "";
        var f = new SelfFacts
        {
            IsServer = Plugin.IsServerProcess,
            CoreVersion = Plugin.Version,
            BootstrapVersion = BootstrapVersion(),
            CoreSha = CoreSha(),
            BootstrapSha = BootIdentityText("Sha256"),
            Record = SelfFiles.ReadRecord(root),
            ModManager = LooksLikeModManager(bootPath),
            DevBridge = DevBridgeLoaded(),
            DevOverride = _devOverride?.Value ?? false,
            Mode = UpdateSettings.Parse(_mode?.Value ?? SelfUpdateSettings.DefaultFor(Plugin.IsServerProcess)),
        };
        f.LedgerFramework = LedgerNames(f.CoreSha, f.BootstrapSha);
        return f;
    }

    private static Assembly? BootstrapAssembly => BootstrapLink.HostType?.Assembly;

    private static string BootIdentityText(string property)
    {
        try
        {
            var t = BootstrapAssembly?.GetType("Progenitor.Bootstrap.BootIdentity");
            return t?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "";
        }
        catch { return ""; }
    }

    private static string? BootstrapPath()
    {
        string p = BootIdentityText("Path");
        if (p.Length > 0) return p;
        try { return BootstrapAssembly?.Location; }
        catch { return null; }
    }

    private static string BootstrapVersion()
    {
        string v = BootIdentityText("Version");
        if (v.Length > 0) return v;
        try
        {
            var version = BootstrapAssembly?.GetName().Version;
            return version == null ? "" : SelfImageReader.FormatVersion(version);
        }
        catch { return ""; }
    }

    private static byte[]? CoreBytes()
    {
        try
        {
            var t = BootstrapAssembly?.GetType("Progenitor.Bootstrap.CoreLoader");
            return t?.GetField("_lastLoadedBytes", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as byte[];
        }
        catch { return null; }
    }

    /// <summary>The loaded Core image's sha256 (the feature table's <c>coreSha</c>), or empty when Bootstrap is absent.</summary>
    internal static string RunningCoreSha()
    {
        try { return CoreSha(); }
        catch { return ""; }
    }

    /// <summary>The loaded Core image's sha256 (the bytes Bootstrap loaded this generation from), cached per byte array.</summary>
    private static string CoreSha()
    {
        var bytes = CoreBytes();
        if (bytes == null || bytes.Length == 0) return "";
        if (ReferenceEquals(bytes, _shaBytes)) return _coreSha;
        _coreSha = SelfFiles.Sha256(bytes);
        _shaBytes = bytes;
        return _coreSha;
    }

    /// <summary>A mod manager's profile: a <c>manifest.json</c> beside Bootstrap (one folder per package) or a known profile path.</summary>
    internal static bool LooksLikeModManager(string bootstrapPath)
    {
        if (string.IsNullOrEmpty(bootstrapPath)) return false;
        try
        {
            string? dir = Path.GetDirectoryName(bootstrapPath);
            if (dir != null && !string.Equals(Path.GetFileName(dir), "plugins", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(dir, "manifest.json"))) return true;
            string p = bootstrapPath.Replace('\\', '/').ToLowerInvariant();
            return p.Contains("/r2modmanplus-local/") || p.Contains("/thunderstore mod manager/") || p.Contains("com.kesomannen.gale");
        }
        catch { return false; }
    }

    private static bool DevBridgeLoaded()
    {
        try
        {
            if (BootstrapLink.TryCaptureAdmissionSnapshot("progenitor.dev", null, null, out var v) && v != null) return v.PluginLoaded;
        }
        catch { /* fall back */ }

        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, "Progenitor.Dev", StringComparison.Ordinal)) return true;
        }
        catch { /* */ }

        return false;
    }

    /// <summary>A ledger <c>framework</c> pair names exactly these Bootstrap and Core images (a server folder this client installed).</summary>
    private static bool LedgerNames(string coreSha, string bootSha)
    {
        if (coreSha.Length == 0 || bootSha.Length == 0) return false;
        try
        {
            var ledger = ServerInstallLedger.TryRead(Paths.GameRootPath);
            if (ledger == null) return false;
            bool core = false, boot = false;
            foreach (var row in ledger.Files)
            {
                if (!string.Equals(row.Category, ServerInstallLedger.CategoryFramework, StringComparison.Ordinal)) continue;
                string leaf = Path.GetFileName(row.Path ?? "");
                if (string.Equals(leaf, SelfPaths.CoreFile, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Sha256, coreSha, StringComparison.OrdinalIgnoreCase)) core = true;
                if (string.Equals(leaf, SelfPaths.BootstrapFile, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Sha256, bootSha, StringComparison.OrdinalIgnoreCase)) boot = true;
            }

            return core && boot;
        }
        catch { return false; }
    }

    // ---- download, swap, status -----------------------------------------------------------------------------

    // ---- the release list ----------------------------------------------------------------------------------

    /// <summary>
    /// Developer testing only: while the dev Thunderstore stand-in is active (<c>[Install] DevThunderstoreUrl</c>), the release
    /// list and its packages come from the stand-in's <c>/progenitor/updates/</c> (tools/local-thunderstore), with no
    /// fallback, and <c>[AutoUpdate] SelfUpdateDevKey</c> (an SPKI base64 public key) is trusted beside the built-in keys.
    /// </summary>
    private static string? DevOrigin()
    {
        try { return ThunderstoreMirror.Base is { } b ? b.GetLeftPart(UriPartial.Authority) : null; }
        catch { return null; }
    }

    private static string FeedUrl() => DevOrigin() is { } dev ? dev + "/progenitor/updates/releases.json" : SelfFeed.PrimaryUrl;

    private static IndexDiskCache FeedCache() => new(Paths.BepInExRootPath, FeedUrl());

    private static System.Collections.Generic.IReadOnlyList<string> Keys()
    {
        var keys = new System.Collections.Generic.List<string>(SelfFeedKeys.Trusted());
        if (DevOrigin() != null && (_devKey?.Value ?? "").Trim() is { Length: > 0 } dev) keys.Add(dev);
        return keys;
    }

    /// <summary>The mod index's stamp (it moves when the Install tab or the join screens read the index).</summary>
    private static string ModIndexStamp()
    {
        try
        {
            string? p = new IndexDiskCache(Paths.BepInExRootPath, CatalogFetchOptions.Default.IndexUrl).StampPath;
            return p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        }
        catch { return ""; }
    }

    private static SelfFeedFetch? _check;
    private static string _checkUrl = "";

    /// <summary>One conditional request for the release list (owner <c>self-update</c>, its backoff), the fallback only on failure.</summary>
    private static async Task<string> CheckFeedAsync(CancellationToken ct)
    {
        string url = FeedUrl();
        if (_check == null || !string.Equals(_checkUrl, url, StringComparison.Ordinal))
        {
            var http = SharedCatalogHttp.Acquire();
            var fetcher = new CatalogFetcher(http, CatalogFetchOptions.Default);
            _check = new SelfFeedFetch((u, validators, max, token) => fetcher.FetchIndexTextAsync(u, max, validators, token),
                new IndexDiskCache(Paths.BepInExRootPath, url), Keys, DevOrigin() != null ? null : SelfFeed.FallbackUrl);
            _checkUrl = url;
        }

        var r = await _check.FetchAsync(ct).ConfigureAwait(false);
        return r.Ok ? (r.NotModified ? "304, the cached release list is current (" + url + ")" : "200, the release list was read and verified (" + url + ")") : "failed: " + r.Error;
    }

    /// <summary>A release package: only its GitHub release asset (redirects only to GitHub's asset hosts), within the bound.</summary>
    private static async Task<(byte[]? Bytes, string Error)> DownloadAsync(string url, long max, CancellationToken ct)
    {
        string? dev = DevOrigin();
        if (!SelfFeed.IsAssetUrl(url, dev)) return (null, "its download is not a Progenitor release on GitHub (" + url + ")");
        using var http = SharedCatalogHttp.Acquire();
        var fetcher = new CatalogFetcher(http, new CatalogFetchOptions { RedirectAllowed = u => SelfFeed.IsAllowedRedirect(u, dev) });
        var r = await fetcher.FetchBytesAsync(url, max, ct).ConfigureAwait(false);
        return r.Ok && r.Bytes != null ? (r.Bytes, "") : (null, r.Error ?? ("status " + r.StatusCode));
    }

    private static string RequestSwap(string reason)
    {
        var t = BootstrapAssembly?.GetType("Progenitor.Bootstrap.CoreLoader");
        var mi = t?.GetMethod("RequestReload", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (mi == null) return "this Bootstrap cannot swap Core";
        return mi.Invoke(null, new object[] { reason }) as string ?? "";
    }

    private static (bool, string, string) ReadReloadStatus()
    {
        var t = BootstrapAssembly?.GetType("Progenitor.Bootstrap.CoreLoader");
        var mi = t?.GetMethod("DescribeStatus", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (mi == null) return (false, "none", "");
        var dump = mi.Invoke(null, Array.Empty<object>());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dump));
        var root = doc.RootElement;
        bool inFlight = root.TryGetProperty("inFlight", out var f) && f.ValueKind == JsonValueKind.True;
        string outcome = "", reason = "";
        if (root.TryGetProperty("lastReload", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            outcome = SelfState.Str(last, "outcome");
            reason = SelfState.Str(last, "reason");
        }

        return (inFlight, outcome, reason);
    }

    // ---- the apply window -----------------------------------------------------------------------------------

    private static string ClientWindow()
    {
        if (!Progenitor.Ui.NativeModsEntry.AtTitleScreen) return "not at the title screen";
        if (ClientUpdates.Busy) return "a mod update is running";
        if (BootstrapLink.IsHostingPrivateGame) return "a private game is hosted";
        string closed = ClientAutoUpdate.WindowNow(ignoreRunner: true);
        return closed == "no window probe" ? "the Mods window has not started yet" : closed;
    }

    /// <summary>
    /// A server applies when it is empty for phase 3's quiet period: <c>[AutoUpdate] EmptyMinutes</c> (default 5) on the
    /// server auto-updater's own empty gate (the time since the last player left, reset by a connect).
    /// </summary>
    private static string ServerWindow()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return "the world is not loaded";
        int minutes = ServerAutoUpdateLive.ReadSettings().EmptyMinutes;
        var gate = ServerAutoUpdate.EmptyState;
        var now = DateTime.UtcNow;
        if (gate.Online < 0)
        {
            // The server auto-updater has not observed the players yet: observe them here, on the same gate.
            int online;
            try { online = Progenitor.Players.PlayerDirectory.Online(world).Count; }
            catch { return "the players could not be read"; }
            gate.Observe(online, now);
        }

        if (gate.Online > 0) return gate.Online == 1 ? "1 player is online" : gate.Online + " players are online";
        if (!gate.Quiet(now, minutes))
        {
            var left = gate.Remaining(now, minutes);
            return "waiting until the server has been empty for " + minutes + " min" + (left is { } l ? " (" + (int)Math.Ceiling(l.TotalSeconds) + " s left)" : "");
        }

        var status = ReadReloadStatus();
        if (status.Item1) return "a Core swap is in flight";
        return "";
    }
}
#endif
