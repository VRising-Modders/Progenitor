using System;
using System.Collections.Generic;
using System.IO;
#if !PROGENITOR_TESTS
using BepInEx.Unity.IL2CPP;
#endif

namespace Progenitor.Install;

/// <summary>Where an <see cref="InstallTime"/> came from, most trusted first.</summary>
internal enum InstallTimeSource
{
    /// <summary>Nothing known: the mod is not installed, or no evidence could be read.</summary>
    None = 0,

    /// <summary>The <c>writtenUtc</c> of a <c>BepInEx/progenitor-install.json</c> row Progenitor wrote.</summary>
    Ledger = 1,

    /// <summary>The plugin file's creation time (a copy or an extraction sets it to when it arrived).</summary>
    FileCreated = 2,

    /// <summary>The plugin file's last-write time, used only when the creation time is unreadable or implausible.</summary>
    FileWritten = 3,
}

/// <summary>When a mod was installed on this machine, and which evidence said so.</summary>
internal readonly struct InstallTime
{
    public InstallTime(DateTime? utc, InstallTimeSource source)
    {
        if (utc == null || source == InstallTimeSource.None)
        {
            Utc = null;
            Source = InstallTimeSource.None;
            return;
        }

        Utc = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        Source = source;
    }

    /// <summary>The install time in UTC, or null when unknown.</summary>
    public DateTime? Utc { get; }

    public InstallTimeSource Source { get; }

    public bool Known => Utc != null;

    /// <summary>The readback name of <see cref="Source"/> (<see cref="InstallTimes.SourceLedger"/> …).</summary>
    public string SourceName => InstallTimes.SourceName(Source);

    public static InstallTime Unknown => default;
}

/// <summary>
/// INSTALLCOLS1: the one answer to "when was this mod installed on this machine". The Install tab's
/// Installed column reads it, and the Keybinds tab's "first installed wins" rule is meant to read the
/// same helper, so the two can never disagree about which mod came first.
///
/// <para><b>Precedence.</b> 1. Progenitor's install ledger (<c>BepInEx/progenitor-install.json</c>): the
/// earliest <c>writtenUtc</c> among the rows that name the mod. A row is rewritten when the local server
/// promotes a pending copy at its next start, so a restart-route install on the local server reads the
/// time of that start, not of the click. 2. The plugin file's creation time — Windows sets it when the
/// file is copied or extracted, whereas the last-write time survives a copy and is often the author's
/// build time. 3. The last-write time, only when the creation time cannot be read or is implausible
/// (before 2000, the 1601 zero stamp, or more than a day in the future).</para>
///
/// <para>Nothing here throws: an unreadable ledger or file is "no evidence", and the next source is asked.
/// The API is kept small and stable on purpose; other branches build on it.</para>
/// </summary>
internal static class InstallTimes
{
    public const string SourceLedger = "ledger";
    public const string SourceFileCreated = "fileCreated";
    public const string SourceFileWritten = "fileWritten";
    public const string SourceNone = "none";

    private static readonly DateTime Floor = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The clock the plausibility check compares against; <see cref="DateTime.UtcNow"/> in the game.</summary>
    internal static Func<DateTime>? ClockUtc { get; set; }

    public static string SourceName(InstallTimeSource source) => source switch
    {
        InstallTimeSource.Ledger => SourceLedger,
        InstallTimeSource.FileCreated => SourceFileCreated,
        InstallTimeSource.FileWritten => SourceFileWritten,
        _ => SourceNone,
    };

    /// <summary>A stamp that can be a real install time: not before 2000 and not more than a day ahead.</summary>
    public static bool Plausible(DateTime? utc)
    {
        if (utc == null) return false;
        var value = utc.Value.Kind == DateTimeKind.Local ? utc.Value.ToUniversalTime() : utc.Value;
        DateTime now;
        try { now = ClockUtc?.Invoke() ?? DateTime.UtcNow; }
        catch { now = DateTime.UtcNow; }
        return value >= Floor && value <= now.AddDays(1);
    }

    /// <summary>The precedence itself: the ledger, then the file's creation time, then its last-write time.</summary>
    public static InstallTime Pick(DateTime? ledgerUtc, DateTime? fileCreatedUtc, DateTime? fileWrittenUtc)
    {
        if (Plausible(ledgerUtc)) return new InstallTime(ledgerUtc, InstallTimeSource.Ledger);
        if (Plausible(fileCreatedUtc)) return new InstallTime(fileCreatedUtc, InstallTimeSource.FileCreated);
        if (Plausible(fileWrittenUtc)) return new InstallTime(fileWrittenUtc, InstallTimeSource.FileWritten);
        return InstallTime.Unknown;
    }

    /// <summary>The earliest plausible stamp of <paramref name="stamps"/>, or null.</summary>
    public static DateTime? Earliest(IEnumerable<DateTime?>? stamps)
    {
        if (stamps == null) return null;
        DateTime? best = null;
        foreach (var stamp in stamps)
        {
            if (!Plausible(stamp)) continue;
            if (best == null || stamp!.Value < best.Value) best = stamp;
        }
        return best;
    }

    /// <summary>
    /// The earliest <c>writtenUtc</c> of the rows of <paramref name="ledger"/> that name the plugin: by
    /// the row's <c>pluginId</c> field, or by the file name its <c>path</c> ends in. Null when none does.
    /// </summary>
    public static DateTime? LedgerTime(ServerInstallLedger? ledger, string? pluginId, string? fileName)
    {
        if (ledger == null) return null;
        string id = (pluginId ?? "").Trim();
        string file = (fileName ?? "").Trim();
        if (id.Length == 0 && file.Length == 0) return null;
        var stamps = new List<DateTime?>();
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var row = ledger.Files[i];
            bool byId = id.Length > 0 && string.Equals(row.PluginId, id, StringComparison.OrdinalIgnoreCase);
            bool byFile = file.Length > 0 && string.Equals(FileNameOf(row.Path), file, StringComparison.OrdinalIgnoreCase);
            if (byId || byFile) stamps.Add(row.WrittenUtc);
        }
        return Earliest(stamps);
    }

    /// <summary>The file's creation and last-write times in UTC; null for any that cannot be read.</summary>
    public static (DateTime? CreatedUtc, DateTime? WrittenUtc) FileTimes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);
        try
        {
            if (!File.Exists(path)) return (null, null);
        }
        catch { return (null, null); }

        DateTime? created = null, written = null;
        try { created = File.GetCreationTimeUtc(path); }
        catch { /* no creation time on this file system */ }
        try { written = File.GetLastWriteTimeUtc(path); }
        catch { /* unreadable */ }
        return (created, written);
    }

    /// <summary>The whole precedence for a plugin file with the ledger already read.</summary>
    public static InstallTime Of(string? pluginId, string? pluginFilePath, ServerInstallLedger? ledger)
    {
        DateTime? fromLedger = LedgerTime(ledger, pluginId, FileNameOf(pluginFilePath));
        if (Plausible(fromLedger)) return Pick(fromLedger, null, null);
        var (created, written) = FileTimes(pluginFilePath);
        return Pick(null, created, written);
    }

    /// <summary>
    /// The whole precedence for a plugin: the install ledger of <paramref name="gameRoot"/> (the folder
    /// that holds <c>BepInEx</c>), then <paramref name="pluginFilePath"/>'s own times.
    /// </summary>
    public static InstallTime Of(string? pluginId, string? pluginFilePath, string? gameRoot)
    {
        ServerInstallLedger? ledger = null;
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            try { ledger = ServerInstallLedger.TryRead(gameRoot!); }
            catch { ledger = null; }
        }
        return Of(pluginId, pluginFilePath, ledger);
    }

    /// <summary>
    /// The file a loaded plugin was read from (<c>PluginInfo.Location</c>), or null when it is not
    /// loaded in this process or the loader does not say. Always null in the game-free test build.
    /// </summary>
    public static string? PluginLocation(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null && plugins.TryGetValue(pluginId!.Trim(), out var info)
                && info != null && !string.IsNullOrWhiteSpace(info.Location))
                return info.Location;
        }
        catch { /* the loader's table is evidence, never a gate */ }
        return null;
#endif
    }

    private static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path!.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p.Substring(slash + 1) : p;
    }
}
