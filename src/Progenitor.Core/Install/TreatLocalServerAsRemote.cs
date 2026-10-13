using System;
using System.IO;

namespace Progenitor.Install;

/// <summary>
/// REMOTETEST1 (requested 2026-10-03): <c>[Install] TreatLocalServerAsRemote</c>. When on, a
/// connected server that is the configured local folder's server is still handled through the
/// remote-install route (<see cref="ServerRouteSelector.Decide"/> sets
/// <see cref="ServerRoute.ForcedRemote"/>), so the remote wire, the admin list and the server's own
/// Thunderstore fetch can be exercised against a server on this PC.
///
/// <para><b>Live edits (REMOTEUNREACH1).</b> BepInEx reads a <c>.cfg</c> once; an edit made while
/// the game runs never reaches the bound entry, so live the switch only took effect after a Core hot
/// swap. Reloading the shared <c>ConfigFile</c> would fire every other subscriber's SettingChanged,
/// and a file watcher is a process-lifetime resource Bootstrap owns. So the value is read straight
/// off the file instead: at most once a second, only when the file's write time moved, a plain text
/// read of this one key — no event, no write-back, nothing that outlives this Core generation. The
/// bound entry is the fallback when the file has no such line.</para>
/// </summary>
internal static class TreatLocalServerAsRemote
{
    public const string Key = Config.ConfigHelper.TreatLocalServerAsRemoteKey;

    /// <summary>Test seam: overrides the config entry and the file.</summary>
    internal static bool? TestValue { get; set; }

    /// <summary>How often the file's write time is looked at (the route is decided many times a frame).</summary>
    internal static TimeSpan RecheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    private static readonly object Gate = new();
    private static string? _path;
    private static DateTime _stamp;
    private static long _length = -1;
    private static bool? _fileValue;
    private static DateTime _checkedUtc = DateTime.MinValue;

    public static bool Resolve()
    {
        if (TestValue is bool test) return test;
        bool? bound = Config.ConfigHelper.TreatLocalServerAsRemoteValue;
        string? path = null;
#if !PROGENITOR_TESTS
        try
        {
            var file = Progenitor.Config.SharedConfig.TryGetOrLog("[" + Config.ConfigHelper.InstallSection + "] " + Key);
            if (file != null)
            {
                if (bound == null) bound = Config.ConfigHelper.BindTreatLocalServerAsRemote(file).Value;
                path = file.ConfigFilePath;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: binding [Install] " + Key + " threw (" + e.Message + "); the default (false) stands.");
        }
#endif
        bool? live = path == null ? null : FromFile(path, DateTime.UtcNow);
        return live ?? bound ?? false;
    }

    /// <summary>
    /// The key's value as the file says it now, or null when the file has no such line (or cannot be
    /// read). Re-reads only when <see cref="RecheckInterval"/> passed and the write time or length moved.
    /// </summary>
    internal static bool? FromFile(string path, DateTime nowUtc)
    {
        lock (Gate)
        {
            bool samePath = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
            if (samePath && nowUtc - _checkedUtc < RecheckInterval) return _fileValue;
            _checkedUtc = nowUtc;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _path = path;
                    _fileValue = null;
                    return null;
                }

                if (samePath && info.LastWriteTimeUtc == _stamp && info.Length == _length) return _fileValue;
                bool? before = _fileValue;
                _fileValue = ParseKey(File.ReadAllText(path));
                bool firstRead = !samePath;
                _path = path;
                _stamp = info.LastWriteTimeUtc;
                _length = info.Length;
                if (!firstRead && before != _fileValue)
                    InstallLog.Info("Install: [Install] " + Key + " is now " + (_fileValue?.ToString().ToLowerInvariant() ?? "unset")
                        + " (read from the edited config file; no restart needed).");
                return _fileValue;
            }
            catch
            {
                return _fileValue;
            }
        }
    }

    /// <summary>The <c>[Install]</c> section's <c>TreatLocalServerAsRemote = true|false</c>, or null when absent or unreadable.</summary>
    internal static bool? ParseKey(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        bool inSection = false;
        foreach (string raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(line.Substring(1, line.Length - 2).Trim(), Config.ConfigHelper.InstallSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(line.Substring(0, eq).Trim(), Key, StringComparison.OrdinalIgnoreCase)) continue;
            string value = line.Substring(eq + 1).Trim();
            if (bool.TryParse(value, out bool parsed)) return parsed;
            return null;
        }

        return null;
    }

    /// <summary>Test seam: forget the cached file read.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _path = null;
            _stamp = default;
            _length = -1;
            _fileValue = null;
            _checkedUtc = DateTime.MinValue;
        }

        TestValue = null;
        RecheckInterval = TimeSpan.FromSeconds(1);
    }
}
