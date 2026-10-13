using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Progenitor.Effects;

#if !PROGENITOR_TESTS
using BepInEx.Configuration;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument policy and result shapes for the client <c>catalog_*</c> ops.
/// Live Unity work lives in <see cref="CatalogAssetOps"/> behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class CatalogAssetOpArgs
{
    internal const string ClientOnlyMessage = "catalog_* ops run on the client side only";
    internal const string UnknownHandle = "unknown handle";

    internal static CatalogLoadRequest ParseLoad(JsonElement args)
    {
        string archiveId = RequiredString(args, "archiveId");
        if (!CatalogAssetIdentity.IsArchiveId(archiveId))
            throw new ArgumentException("args.archiveId must be a 32-char hex ContentArchive id");
        string pathRaw = RequiredString(args, "pathID");
        if (!CatalogAssetIdentity.TryParsePathId(pathRaw, out ulong pathId))
            throw new ArgumentException("args.pathID must be an unsigned integer string");
        long budget = OptionalInt64(args, "budgetBytes", AssetAcquisitionRequest.DefaultBudgetBytes);
        if (budget < 0) budget = 0;
        int timeout = OptionalInt32(args, "timeoutMs", AssetAcquisitionRequest.DefaultTimeoutMs);
        if (timeout < 0) timeout = 0;
        return new CatalogLoadRequest(archiveId, pathId, budget, timeout);
    }

    /// <summary>
    /// Bridge JSON must not carry GetLoadedArchivesStateDump verbatim (a live RTC dump is
    /// tens of megabytes). Opt-in <c>fullDump</c> inlines the text only when it fits
    /// <see cref="MaxInlineRtcDumpChars"/>; otherwise it is written to a file and the
    /// response names that path.
    /// </summary>
    internal const int MaxInlineRtcDumpChars = 64 * 1024;

    internal const int MaxMatchingRtcLines = 64;

    internal const int MaxMatchingRtcChars = 32 * 1024;

    internal static CatalogResidencyRequest ParseResidency(JsonElement args)
    {
        string name = RequiredString(args, "name");
        string? archiveId = OptionalString(args, "archiveId");
        if (archiveId != null && archiveId.Length > 0 && !CatalogAssetIdentity.IsArchiveId(archiveId))
            throw new ArgumentException("args.archiveId must be a 32-char hex ContentArchive id");
        string? pathRaw = OptionalString(args, "pathID");
        ulong? pathId = null;
        if (!string.IsNullOrEmpty(pathRaw))
        {
            if (!CatalogAssetIdentity.TryParsePathId(pathRaw, out ulong parsed))
                throw new ArgumentException("args.pathID must be an unsigned integer string");
            pathId = parsed;
        }

        return new CatalogResidencyRequest(name, archiveId, pathId, OptionalBool(args, "fullDump", false));
    }

    internal static string ParseHandle(JsonElement args)
    {
        string handle = RequiredString(args, "handle");
        if (handle.Length == 0) throw new ArgumentException("args.handle is required");
        return handle;
    }

    internal static CatalogPreviewRequest ParsePreview(JsonElement args)
    {
        string handle = ParseHandle(args);
        string host = OptionalString(args, "host") ?? "menu";
        host = host.Trim().ToLowerInvariant();
        if (host is not "menu" and not "world")
            throw new ArgumentException("args.host must be menu or world");
        int? seconds = null;
        int s = OptionalInt32(args, "seconds", -1);
        if (s >= 0) seconds = s;
        return new CatalogPreviewRequest(handle, host, seconds);
    }

    internal static object LoadRefused(AssetAcquisitionRequest request, string reason, long knownBytes) =>
        new Dictionary<string, object?>
        {
            ["status"] = StatusToken(AssetAcquisitionStatus.Refused),
            ["reason"] = reason,
            ["handle"] = null,
            ["identity"] = Identity(request.ArchiveId, request.PathId, "", ""),
            ["filesLoaded"] = 0,
            ["bytesCompressed"] = knownBytes,
            ["elapsedMs"] = 0,
            ["objectCount"] = 0,
            ["loadingStatus"] = null,
            ["optIn"] = AssetAcquisitionOptIn.Granted,
        };

    internal static object LoadFromHandle(IAssetAcquisitionHandle handle, long bytesCompressed, int elapsedMs) =>
        new Dictionary<string, object?>
        {
            ["status"] = StatusToken(handle.Status),
            ["reason"] = handle.Reason,
            ["handle"] = handle.HandleId,
            ["identity"] = Identity(handle.ArchiveId, handle.PathId, handle.Name, handle.UnityType),
            ["filesLoaded"] = handle.OwnedFileCount,
            ["bytesCompressed"] = bytesCompressed,
            ["elapsedMs"] = elapsedMs,
            ["objectCount"] = handle.ObjectCount,
            ["loadingStatus"] = handle.Status == AssetAcquisitionStatus.Ready ? "Completed" : null,
            ["optIn"] = AssetAcquisitionOptIn.Granted,
        };

    internal static object StatusFromHandle(IAssetAcquisitionHandle handle) =>
        new Dictionary<string, object?>
        {
            ["status"] = StatusToken(handle.Status),
            ["reason"] = handle.Reason,
            ["visible"] = handle.Visible,
            ["ownedFiles"] = handle.OwnedFileCount,
            ["ownedArchives"] = handle.OwnedArchiveCount,
            ["stale"] = handle.IsStale,
            ["preview"] = handle.Visible,
        };

    internal static object ReleaseFromHandle(int files, int archives, bool previewDestroyed) =>
        new Dictionary<string, object?>
        {
            ["releasedFiles"] = files,
            ["releasedArchives"] = archives,
            ["previewDestroyed"] = previewDestroyed,
            ["pgMounted"] = AssetAcquisition.LiveArchiveCount,
            ["pgFiles"] = AssetAcquisition.LiveFileCount,
        };

    internal static object ResidencyFromReport(
        AssetResidencyReport report,
        bool fullDump = false,
        string? dumpDirectory = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["scan"] = new Dictionary<string, object?>
            {
                ["api"] = report.ScanApi,
                ["type"] = "GameObject",
                ["name"] = report.Name,
                ["count"] = report.NameCount,
                ["instanceIds"] = report.InstanceIds,
            },
            ["rtcDump"] = SummarizeRtcDump(report.RtcDump ?? "", report.Name, report.ArchiveId),
            ["rtcMentionsTarget"] = report.RtcMentionsTarget,
            ["pg"] = new Dictionary<string, object?>
            {
                ["namespace"] = CatalogAssetIdentity.NamespaceName,
                ["mounted"] = report.PgMounted,
                ["files"] = report.PgFiles,
                ["icallsResolved"] = report.IcallsResolved,
            },
            ["abortIdentity"] = report.AbortIdentity,
            ["catalogSha256"] = report.CatalogSha256,
        };

        if (!fullDump) return body;

        string dump = report.RtcDump ?? "";
        body["rtcDumpBytes"] = dump.Length;
        if (dump.Length <= MaxInlineRtcDumpChars)
        {
            body["rtcDumpFull"] = dump;
            return body;
        }

        string path = WriteRtcDumpFile(dump, report.Name, dumpDirectory);
        body["rtcDumpFile"] = path;
        return body;
    }

    /// <summary>
    /// Count + only the dump lines that mention <paramref name="name"/> or
    /// <paramref name="archiveId"/>. The abort/scan decision stays on
    /// <see cref="AssetResidencyReport"/>; this is the wire payload only.
    /// </summary>
    internal static Dictionary<string, object?> SummarizeRtcDump(string dump, string name, string? archiveId)
    {
        dump ??= "";
        ParseRtcHeader(dump, out int contentFiles, out int activeObjects, out int lineCount);
        var matching = new List<string>();
        int matchingChars = 0;
        foreach (var line in SplitRtcLines(dump))
        {
            if (!RtcLineMentionsTarget(line, name, archiveId)) continue;
            if (matching.Count >= MaxMatchingRtcLines) break;
            if (matchingChars + line.Length > MaxMatchingRtcChars) break;
            matching.Add(line);
            matchingChars += line.Length;
        }

        return new Dictionary<string, object?>
        {
            ["contentFiles"] = contentFiles,
            ["activeObjects"] = activeObjects,
            ["chars"] = dump.Length,
            ["lineCount"] = lineCount,
            ["matchingCount"] = matching.Count,
            ["matching"] = matching,
        };
    }

    internal static string WriteRtcDumpFile(string dump, string name, string? dumpDirectory)
    {
        string dir = string.IsNullOrWhiteSpace(dumpDirectory)
            ? Path.Combine(Path.GetTempPath(), "progenitor-catalog-residency")
            : dumpDirectory!;
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "rtc-dump-" + SanitizeFileToken(name) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".txt");
        File.WriteAllText(file, dump ?? "");
        return file;
    }

    internal static string StatusToken(AssetAcquisitionStatus status) => status switch
    {
        AssetAcquisitionStatus.Queued => "queued",
        AssetAcquisitionStatus.Loading => "loading",
        AssetAcquisitionStatus.Ready => "ready",
        AssetAcquisitionStatus.Error => "error",
        AssetAcquisitionStatus.Refused => "refused",
        _ => "error",
    };

    static object Identity(string archiveId, ulong pathId, string name, string unityType) =>
        new Dictionary<string, object?>
        {
            ["archiveId"] = archiveId,
            ["pathID"] = CatalogAssetIdentity.FormatPathId(pathId),
            ["name"] = name,
            ["unityType"] = unityType,
        };

    static string RequiredString(JsonElement args, string name)
    {
        string? s = OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("args." + name + " is required");
        return s!;
    }

    static string? OptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();
        return el.GetString();
    }

    static int OptionalInt32(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return fallback;
    }

    static long OptionalInt64(JsonElement args, string name, long fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return fallback;
    }

    static bool OptionalBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n != 0;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n != 0;
        }
        return fallback;
    }

    static readonly Regex RtcHeaderFilesAndObjects = new(
        @"\[(\d+)x Active ContentFiles, (\d+)x Active Objects\]",
        RegexOptions.CultureInvariant);

    static readonly Regex RtcHeaderFilesOnly = new(
        @"\[All Active ContentFiles \((\d+)x\)\]",
        RegexOptions.CultureInvariant);

    static void ParseRtcHeader(string dump, out int contentFiles, out int activeObjects, out int lineCount)
    {
        contentFiles = 0;
        activeObjects = 0;
        lineCount = 0;
        if (string.IsNullOrEmpty(dump)) return;
        var filesAndObjects = RtcHeaderFilesAndObjects.Match(dump);
        if (filesAndObjects.Success)
        {
            contentFiles = int.Parse(filesAndObjects.Groups[1].Value, CultureInfo.InvariantCulture);
            activeObjects = int.Parse(filesAndObjects.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var filesOnly = RtcHeaderFilesOnly.Match(dump);
            if (filesOnly.Success)
                contentFiles = int.Parse(filesOnly.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        foreach (var _ in SplitRtcLines(dump)) lineCount++;
    }

    static IEnumerable<string> SplitRtcLines(string dump)
    {
        if (string.IsNullOrEmpty(dump)) yield break;
        using var reader = new StringReader(dump);
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }

    static bool RtcLineMentionsTarget(string line, string name, string? archiveId)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (!string.IsNullOrEmpty(name) && line.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(archiveId) && line.IndexOf(archiveId, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    static string SanitizeFileToken(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') continue;
            chars[i] = '_';
        }
        var token = new string(chars);
        return token.Length <= 48 ? token : token.Substring(0, 48);
    }

    internal readonly struct CatalogLoadRequest
    {
        public CatalogLoadRequest(string archiveId, ulong pathId, long budgetBytes, int timeoutMs)
        {
            ArchiveId = archiveId;
            PathId = pathId;
            BudgetBytes = budgetBytes;
            TimeoutMs = timeoutMs;
        }

        public string ArchiveId { get; }
        public ulong PathId { get; }
        public long BudgetBytes { get; }
        public int TimeoutMs { get; }

        public AssetAcquisitionRequest ToRequest() =>
            new(ArchiveId, PathId, BudgetBytes, TimeoutMs);
    }

    internal readonly struct CatalogResidencyRequest
    {
        public CatalogResidencyRequest(string name, string? archiveId, ulong? pathId, bool fullDump = false)
        {
            Name = name;
            ArchiveId = archiveId;
            PathId = pathId;
            FullDump = fullDump;
        }

        public string Name { get; }
        public string? ArchiveId { get; }
        public ulong? PathId { get; }
        public bool FullDump { get; }
    }

    internal readonly struct CatalogPreviewRequest
    {
        public CatalogPreviewRequest(string handle, string host, int? seconds)
        {
            Handle = handle;
            Host = host;
            Seconds = seconds;
        }

        public string Handle { get; }
        public string Host { get; }
        public int? Seconds { get; }
    }
}

/// <summary>
/// Client Dev ops <c>catalog_residency</c> / <c>catalog_load</c> / <c>catalog_status</c> /
/// <c>catalog_preview</c> / <c>catalog_release</c>. Acquisition calls refuse unless
/// <see cref="AssetAcquisitionOptIn.Granted"/>.
/// </summary>
internal static class CatalogAssetOps
{
#if !PROGENITOR_TESTS
    public static void Bind(ConfigFile config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        var entry = config.Bind(
            AssetAcquisitionOptIn.ConfigSection,
            AssetAcquisitionOptIn.ConfigKey,
            false,
            "DEV ONLY, off by default. Together with env PROGENITOR_CATALOG_ACQUIRE=1, allows the first ContentNamespace / ArchiveFileInterface / ContentLoadInterface call. Do not enable without an explicit live opt-in.");
        AssetAcquisitionOptIn.ConfigAllowed = entry.Value;
        AssetAcquisition.InstallLiveLoader();
    }

    public static void Shutdown()
    {
        try { AssetAcquisition.RemoveAll(typeof(CatalogAssetOps)); }
        catch { /* */ }
        AssetAcquisitionOptIn.ConfigAllowed = false;
    }

    public static object Residency(JsonElement args)
    {
        EnsureClient();
        var req = CatalogAssetOpArgs.ParseResidency(args);
        var report = AssetAcquisition.ScanResidency(req.Name, req.ArchiveId);
        return CatalogAssetOpArgs.ResidencyFromReport(report, req.FullDump);
    }

    public static object Load(JsonElement args)
    {
        EnsureClient();
        var parsed = CatalogAssetOpArgs.ParseLoad(args);
        var request = parsed.ToRequest();
        string? refuse = AssetAcquisitionRequest.RefuseBeforeMount(request, out long knownBytes);
        if (refuse != null)
            return CatalogAssetOpArgs.LoadRefused(request, refuse, knownBytes);
        if (!AssetAcquisitionOptIn.Granted)
            return CatalogAssetOpArgs.LoadRefused(request, AssetAcquisitionReasons.OptInRequired, knownBytes);

        var started = DateTime.UtcNow;
        var handle = AssetAcquisition.Acquire(request, owner: typeof(CatalogAssetOps), pluginId: MyPluginInfo.PLUGIN_GUID);
        int elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        return CatalogAssetOpArgs.LoadFromHandle(handle, handle.BytesCompressed, elapsed);
    }

    public static object Status(JsonElement args)
    {
        EnsureClient();
        string id = CatalogAssetOpArgs.ParseHandle(args);
        var handle = AssetAcquisition.Find(id) ?? throw new ArgumentException(CatalogAssetOpArgs.UnknownHandle);
        return CatalogAssetOpArgs.StatusFromHandle(handle);
    }

    public static object Preview(JsonElement args)
    {
        EnsureClient();
        var req = CatalogAssetOpArgs.ParsePreview(args);
        var handle = AssetAcquisition.Find(req.Handle) ?? throw new ArgumentException(CatalogAssetOpArgs.UnknownHandle);
        var result = AssetAcquisition.Preview(handle, hostMenu: req.Host == "menu");
        return new Dictionary<string, object?>
        {
            ["session"] = handle.HandleId,
            ["hosted"] = result.Hosted,
            ["unsupported"] = result.Unsupported,
            ["cloneName"] = result.CloneName,
            ["particlePlaying"] = result.ParticlePlaying,
            ["ok"] = result.Ok,
        };
    }

    public static object Release(JsonElement args)
    {
        EnsureClient();
        string id = CatalogAssetOpArgs.ParseHandle(args);
        var handle = AssetAcquisition.Find(id) ?? throw new ArgumentException(CatalogAssetOpArgs.UnknownHandle);
        int files = handle.OwnedFileCount;
        int archives = handle.OwnedArchiveCount;
        bool visible = handle.Visible;
        handle.Dispose();
        return CatalogAssetOpArgs.ReleaseFromHandle(files, archives, visible);
    }

    static void EnsureClient()
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException(CatalogAssetOpArgs.ClientOnlyMessage);
    }
#endif
}
