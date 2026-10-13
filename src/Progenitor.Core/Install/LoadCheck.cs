using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Install;

/// <summary>AUTOUPDATE2: what a load check concluded about one written image.</summary>
internal enum LoadVerdict
{
    /// <summary>The image is loaded and its plugin did not report a failed load.</summary>
    Loaded = 0,

    /// <summary>The load threw (Core's registry, or the reloader's failed-load record): a real failure.</summary>
    Failed = 1,

    /// <summary>Not loaded yet, or not registered yet: keep waiting.</summary>
    Waiting = 2,

    /// <summary>Nothing can say which image is loaded (an older Bootstrap and no reloader record): unverified.</summary>
    Unknown = 3,
}

/// <summary>The reloader's record of one plugin file (<c>BepInEx/&lt;stem&gt;.delta-state</c>, written at each load).</summary>
internal sealed class ReloadRecord
{
    public bool Loaded { get; init; }

    /// <summary>The loaded image's sha256 (empty after a failed load).</summary>
    public string ImageSha { get; init; } = "";

    /// <summary>Why the load threw, or empty.</summary>
    public string Failure { get; init; } = "";

    public DateTime WrittenUtc { get; init; }

    /// <summary>Read <c>&lt;BepInEx&gt;/&lt;stem&gt;.delta-state</c>, or null when it is missing or unreadable.</summary>
    public static ReloadRecord? Read(string? bepInExRoot, string? stem)
    {
        if (string.IsNullOrEmpty(bepInExRoot) || string.IsNullOrEmpty(stem)) return null;
        try
        {
            string path = Path.Combine(bepInExRoot!, stem + Reload.DeltaApply.StateExtension);
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path), File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    public static ReloadRecord? Parse(string? text, DateTime writtenUtc)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text!.Replace("\r", "").Split('\n'))
        {
            int eq = raw.IndexOf('=');
            if (eq > 0) map[raw.Substring(0, eq)] = raw.Substring(eq + 1);
        }

        if (!map.ContainsKey("plugin")) return null;
        return new ReloadRecord
        {
            // A record from before the loaded= field means a successful load.
            Loaded = !map.TryGetValue("loaded", out var l) || l != "0",
            ImageSha = map.TryGetValue("baseline", out var b) ? b.Trim().ToLowerInvariant() : "",
            Failure = map.TryGetValue("failure", out var f) ? f.Trim() : "",
            WrittenUtc = writtenUtc,
        };
    }
}

/// <summary>Everything a load check reads about one plugin, from the live probes (tests set it directly).</summary>
internal sealed class LoadFacts
{
    /// <summary>Bootstrap's admission snapshot: the reloader holds the plugin, and its image sha256; null on an older Bootstrap.</summary>
    public (bool Loaded, string Sha)? Snapshot { get; init; }

    /// <summary>Core's registry outcome for a Progenitor plugin (<c>Loading</c>, <c>Loaded</c>, <c>Failed</c>, …), or null for a plain BepInEx plugin.</summary>
    public string? Registry { get; init; }

    /// <summary>The reloader's record for the file, or null.</summary>
    public ReloadRecord? Record { get; init; }

    public bool LoaderBusy { get; init; }
}

/// <summary>
/// AUTOUPDATE2 review M1/M2: did the image just written load? Detected as failed: a Progenitor plugin whose <c>OnLoad</c>
/// threw (Core's registry says <c>Failed</c>), and any plugin whose load threw in the reloader (its failed-load record,
/// written after the write). Loaded: the reloader holds the image (snapshot sha256 or its record), and a Progenitor plugin
/// is registered <c>Loaded</c>. Not detectable: a plugin that loads but misbehaves later, and a load whose exception the
/// plugin itself swallows. With neither the snapshot nor a record the answer is <see cref="LoadVerdict.Unknown"/>, never
/// success.
/// </summary>
internal static class LoadCheck
{
    /// <summary>The first wait.</summary>
    public const double WaitSeconds = 15;

    /// <summary>The longest the wait is extended while the loader is busy (a Core swap or another reload).</summary>
    public const double MaxWaitSeconds = 120;

    public static (LoadVerdict Verdict, string Detail) Judge(LoadFacts f, string sha, DateTime writtenUtc)
    {
        string want = (sha ?? "").ToLowerInvariant();
        var record = f.Record != null && f.Record.WrittenUtc >= writtenUtc.AddSeconds(-2) ? f.Record : null;
        if (record != null && !record.Loaded && record.Failure.Length > 0)
            return (LoadVerdict.Failed, "Its load threw: " + record.Failure);

        bool snapshotSame = f.Snapshot is { } s && string.Equals(s.Sha, want, StringComparison.OrdinalIgnoreCase);
        bool recordSame = record != null && record.Loaded && string.Equals(record.ImageSha, want, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(f.Registry, "Failed", StringComparison.OrdinalIgnoreCase) && (snapshotSame || recordSame))
            return (LoadVerdict.Failed, "Its load threw an exception (see the log).");

        bool held = (f.Snapshot is { Loaded: true } && snapshotSame) || recordSame;
        if (held)
        {
            // Turned off by the player (SkippedOff) or dormant on this side: the image is in place and its load did not fail.
            if (f.Registry == null || f.Registry is "Loaded" or "SkippedOff" or "Dormant") return (LoadVerdict.Loaded, "");
            return (LoadVerdict.Waiting, "loaded by the reloader; its plugin is not registered as loaded yet (" + f.Registry + ")");
        }

        if (f.Snapshot == null && f.Record == null) return (LoadVerdict.Unknown, "the loader cannot report the loaded image");
        return (LoadVerdict.Waiting, f.Snapshot is { Loaded: true } ? "another image of it is loaded" : "it is not loaded yet");
    }

    /// <summary>The wait's deadline: <see cref="WaitSeconds"/> from the write, pushed back while the loader is busy, never past <see cref="MaxWaitSeconds"/>.</summary>
    public static bool PastDeadline(double elapsed, double busySeconds) =>
        elapsed >= Math.Min(MaxWaitSeconds, WaitSeconds + Math.Max(0, busySeconds));

    public static string Describe(LoadVerdict v) => v.ToString().ToLower(CultureInfo.InvariantCulture);
}
